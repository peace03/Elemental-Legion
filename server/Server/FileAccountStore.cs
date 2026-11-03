using System.Security.Cryptography;
using System.Text.Json;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

public sealed record AccountIdentity(string Id, string Nickname);
public sealed record IssuedAccount(AccountIdentity Account, byte[] Token);

/// <summary>계정의 표시 이름, 인증 키 검증과 운영자 폐기를 분리한다.</summary>
public interface IAccountStore
{
    IssuedAccount Create(string nickname);
    AccountIdentity? Authenticate(ReadOnlySpan<byte> token);
    bool IsActive(string accountId);
    bool Revoke(string accountId);
}

/// <summary>계정별 파일에 해시만 저장하며 정상 기록을 마친 뒤 키를 발급한다.</summary>
public sealed class FileAccountStore : IAccountStore
{
    public const int TokenBytes = 32;
    private const int Version = 1;
    private const int MaxRecordBytes = 512;
    private const int MaxAccounts = 100_000;
    private readonly string directory;
    private readonly object gate = new();
    private readonly Dictionary<string, AccountIdentity> byHash = new(StringComparer.Ordinal);

    private sealed record AccountRecord(int Version, string Id, string Nickname, string Hash);

    public FileAccountStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        foreach (string path in Directory.EnumerateFiles(this.directory, "*.account"))
        {
            AccountRecord account = Read(path, Path.GetFileNameWithoutExtension(path));
            if (!byHash.TryAdd(account.Hash, new AccountIdentity(account.Id, account.Nickname)) || byHash.Count > MaxAccounts)
                throw new InvalidDataException("계정 저장소의 중복 키 또는 저장 한도 오류입니다.");
        }
    }

    public IssuedAccount Create(string nickname)
    {
        if (!PlayerNickname.IsValid(nickname)) throw new ArgumentException("유효하지 않은 닉네임입니다.", nameof(nickname));
        lock (gate)
        {
            if (byHash.Count >= MaxAccounts) throw new InvalidOperationException("계정 저장 한도에 도달했습니다.");
            byte[] token = RandomNumberGenerator.GetBytes(TokenBytes);
            var identity = new AccountIdentity(Guid.NewGuid().ToString("N"), nickname);
            string hash = HashToken(token);
            string path = AccountPath(identity.Id);
            string pending = path + ".tmp";
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new AccountRecord(Version, identity.Id, nickname, hash));
                    stream.Flush(true);
                }
                _ = Read(pending, identity.Id);
                File.Move(pending, path);
                byHash.Add(hash, identity);
                return new IssuedAccount(identity, token);
            }
            finally { File.Delete(pending); }
        }
    }

    public AccountIdentity? Authenticate(ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenBytes) return null;
        lock (gate)
        {
            return byHash.TryGetValue(HashToken(token), out AccountIdentity? account) && IsActive(account.Id) ? account : null;
        }
    }

    public bool IsActive(string accountId) => IsAccountId(accountId) && File.Exists(AccountPath(accountId));

    public bool Revoke(string accountId)
    {
        if (!IsAccountId(accountId)) return false;
        lock (gate)
        {
            string path = AccountPath(accountId);
            if (!File.Exists(path)) return false;
            AccountRecord account = Read(path, accountId);
            File.Move(path, Path.ChangeExtension(path, ".revoked"));
            byHash.Remove(account.Hash);
            return true;
        }
    }

    private string AccountPath(string accountId) => Path.Combine(directory, accountId + ".account");

    private static bool IsAccountId(string id) => Guid.TryParseExact(id, "N", out Guid value) && value != Guid.Empty && value.ToString("N") == id;

    private static string HashToken(ReadOnlySpan<byte> token) => Convert.ToHexString(SHA256.HashData(token));

    private static AccountRecord Read(string path, string expectedId)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaxRecordBytes) throw new InvalidDataException("계정 저장 형식이 올바르지 않습니다.");
        AccountRecord? account;
        try { account = JsonSerializer.Deserialize<AccountRecord>(stream); }
        catch (JsonException error) { throw new InvalidDataException("계정 저장 형식이 올바르지 않습니다.", error); }
        if (account == null || account.Version != Version || account.Id != expectedId || !IsAccountId(account.Id) ||
            !PlayerNickname.IsValid(account.Nickname) || account.Hash?.Length != TokenBytes * 2 ||
            account.Hash.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("계정 저장 형식이 올바르지 않습니다.");
        return account;
    }
}
