using System.Net;
using System.Security.Cryptography;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ElementalLegion.Server;

public sealed partial class UdpGameServer
{
    private readonly IAccountStore? accountStore;
    private readonly Dictionary<string, int> accountOwners;
    private readonly LanRoomOptions? lanRoom;
    private readonly ChatRouter chat;
    private readonly Dictionary<int, Admission> pending = new();
    private readonly List<LobbyPeer> lobby = new();
    private readonly List<int> admissionsDone = new();
    private NetManager? discovery;
    private readonly long launchAt = Environment.TickCount64;
    private long nextDiscoveryAt, accountWindowEnd;
    private int accountsInWindow;
    private bool hostClaimed, hostJoined, roomStarted, roomFinished;
    private readonly List<AiDifficulty> lobbyAiDifficulties = new();
    private readonly List<int> lobbyAiColors = new();
    private int lobbyAiCount => lobbyAiDifficulties.Count;

    private sealed record Admission(NetPeer Peer, int Element, Task<IssuedAccount?> Account, int Bytes, bool Host, long At);
    private sealed record LobbyPeer(NetPeer Peer, IssuedAccount Account, int Element, int Bytes)
    {
        public int ChosenElement = Element;
        public bool Ready;
        public int ColorId;
        public long NextCommandAt;
    }

    private sealed class LanAccounts : IAccountStore
    {
        private readonly object sync = new();
        private readonly Dictionary<string, AccountIdentity> keys = new();
        public IssuedAccount Create(string nickname)
        {
            lock (sync)
            {
                if (keys.Count >= 128) throw new IOException("LAN 계정 한도를 초과했습니다.");
                var identity = new AccountIdentity(Guid.NewGuid().ToString("N"), nickname);
                byte[] token = RandomNumberGenerator.GetBytes(Protocol.AccountKeyBytes);
                keys.Add(Convert.ToHexString(SHA256.HashData(token)), identity);
                return new IssuedAccount(identity, token);
            }
        }
        public AccountIdentity? Authenticate(ReadOnlySpan<byte> token)
        {
            lock (sync) return token.Length == Protocol.AccountKeyBytes ?
                keys.GetValueOrDefault(Convert.ToHexString(SHA256.HashData(token))) : null;
        }
        public bool IsActive(string id) { lock (sync) return keys.Values.Any(account => account.Id == id); }
        public bool Revoke(string id)
        {
            lock (sync)
            {
                string? key = keys.FirstOrDefault(item => item.Value.Id == id).Key;
                return key != null && keys.Remove(key);
            }
        }
    }

    public bool RoomClosed => lanRoom != null && (roomStarted
        ? roomFinished || Simulation.ConnectedCount == 0 && !Simulation.World.HasGhosts
        : lobby.Count == 0 && pending.Count == 0 && (hostJoined || Environment.TickCount64 - launchAt > 15_000));
    internal bool RoomStarted => roomStarted;

    internal Dictionary<string, int> CaptureAccounts()
    {
        if (lanRoom == null)
            foreach (string id in accountOwners.Where(binding => !Simulation.World.TryGetView(binding.Value, out _)).Select(binding => binding.Key).ToArray())
                accountOwners.Remove(id);
        return new Dictionary<string, int>(accountOwners);
    }

    private void QueueAdmission(ConnectionRequest request, int element, string nickname, byte[]? key, byte[]? proof, int bytes)
    {
        IPAddress address = request.RemoteEndPoint.Address;
        bool host = lanRoom != null && !hostClaimed && proof != null && IPAddress.IsLoopback(address) &&
            CryptographicOperations.FixedTimeEquals(proof, lanRoom.HostProof);
        JoinFailure failure = JoinFailure.None;
        if (pending.Count >= rules.MaxPlayers) failure = JoinFailure.WorldFull;
        else if (!IsLocal(address) || lanRoom != null && (!host && !hostJoined || proof != null && !host))
            failure = JoinFailure.Malformed;
        else if (lanRoom != null && roomStarted && key == null) failure = JoinFailure.RoomStarted;
        else if (lanRoom != null && !roomStarted && lobby.Count + pending.Count + lobbyAiCount >= lanRoom.Capacity) failure = JoinFailure.WorldFull;
        long now = Environment.TickCount64;
        if (now >= accountWindowEnd)
        {
            accountWindowEnd = now + 1000;
            accountsInWindow = 0;
        }
        if (failure == JoinFailure.None && key == null && accountsInWindow >= rules.MaxPlayers) failure = JoinFailure.StorageUnavailable;
        if (failure != JoinFailure.None)
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            writer.Reset(); writer.Put((byte)failure); request.Reject(writer);
            return;
        }
        if (host) hostClaimed = true;
        if (key == null) accountsInWindow++;
        NetPeer peer = request.Accept();
        Task<IssuedAccount?> authenticate = Task.Run<IssuedAccount?>(() =>
        {
            if (key == null) return accountStore!.Create(nickname);
            try
            {
                var existing = accountStore!.Authenticate(key);
                return existing == null ? null : new IssuedAccount(existing, Array.Empty<byte>());
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        });
        pending.Add(peer.Id, new Admission(peer, element, authenticate, bytes, host, now));
    }

    private void ProcessAdmissions()
    {
        admissionsDone.Clear();
        foreach (var item in pending)
        {
            Admission entry = item.Value;
            bool timedOut = Environment.TickCount64 - entry.At > 8_000;
            if (!timedOut && (!entry.Account.IsCompleted || entry.Peer.ConnectionState != LiteNetLib.ConnectionState.Connected)) continue;
            admissionsDone.Add(item.Key);
            JoinFailure failure = JoinFailure.StorageUnavailable;
            if (!timedOut && entry.Account.IsCompletedSuccessfully)
            {
                IssuedAccount? account = entry.Account.Result;
                failure = account == null ? JoinFailure.InvalidResumeKey : Admit(entry, account);
            }
            if (failure != JoinFailure.None)
            {
                if (entry.Host) hostClaimed = false;
                DiscardAdmission(entry);
                manager.DisconnectPeer(entry.Peer, [(byte)PacketKind.JoinError, (byte)failure]);
            }
        }
        foreach (int id in admissionsDone) pending.Remove(id);
    }

    private static void DiscardAdmission(Admission entry)
    {
        _ = entry.Account.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result != null) CryptographicOperations.ZeroMemory(task.Result.Token);
            else if (task.IsFaulted) _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private JoinFailure Admit(Admission entry, IssuedAccount account)
    {
        string id = account.Account.Id;
        if (lanRoom != null && !roomStarted)
        {
            if (lobby.Any(member => member.Account.Account.Id == id)) return JoinFailure.AlreadyConnected;
            if (lobby.Count + lobbyAiCount >= lanRoom.Capacity) return JoinFailure.WorldFull;
            lobby.Add(new LobbyPeer(entry.Peer, account, entry.Element, entry.Bytes)
            { ColorId = LobbyColors.FirstAvailable(lobby.Select(member => member.ColorId), lobbyAiColors) });
            if (entry.Host)
            {
                hostJoined = true;
                CryptographicOperations.ZeroMemory(lanRoom.HostProof);
            }
            PublishLobby();
            return JoinFailure.None;
        }

        int owner;
        bool resumed = false;
        if (accountOwners.TryGetValue(id, out owner) && Simulation.World.TryGetView(owner, out _))
        {
            if (Simulation.ConnectedOwners.Contains(owner)) return JoinFailure.AlreadyConnected;
            if (!Simulation.TryResume(owner)) return JoinFailure.InvalidResumeKey;
            resumed = true;
        }
        else
        {
            if (lanRoom != null) return JoinFailure.RoomStarted;
            var result = Simulation.TryJoin(entry.Element, out owner, account.Account.Nickname);
            if (result != JoinFailure.None) return result;
            accountOwners[id] = owner;
        }
        Attach(entry.Peer, owner, account, entry.Bytes, resumed);
        return JoinFailure.None;
    }

    private void ReceiveLobby(NetPeer peer, NetPacketReader reader)
    {
        if (lanRoom == null || roomStarted || reader.AvailableBytes == 0) return;
        int index = lobby.FindIndex(member => ReferenceEquals(member.Peer, peer));
        if (index < 0) return;
        LobbyPeer member = lobby[index];
        long now = Environment.TickCount64;
        if (now < member.NextCommandAt) return;
        member.NextCommandAt = now + 1000 / rules.CommandRateLimit;
        PacketKind kind = (PacketKind)reader.PeekByte();
        if (kind == PacketKind.LobbyReady && M5Protocol.TryReadLobbyReady(reader, rules, out int element, out bool ready))
        {
            member.ChosenElement = element; member.Ready = ready;
            PublishLobby();
            return;
        }
        if (kind == PacketKind.LobbyColor && M5Protocol.TryReadLobbyColor(reader, out int colorId))
        {
            if (member.Ready) { LobbyFailure(peer, LobbyError.InvalidState); return; }
            LobbyError? error = LobbyColors.Select(colorId, lobby.Where(other => !ReferenceEquals(other, member)).Select(other => other.ColorId), lobbyAiColors);
            if (error.HasValue) { LobbyFailure(peer, error.Value); return; }
            member.ColorId = colorId;
            PublishLobby();
            return;
        }
        if (kind == PacketKind.LobbyAiCount && M5Protocol.TryReadLobbyAiCount(reader, out int count))
        {
            if (index != 0) { LobbyFailure(peer, LobbyError.NotHost); return; }
            if (lobby.Count + pending.Count + count > lanRoom.Capacity) { LobbyFailure(peer, LobbyError.RoomFull); return; }
            if (count < lobbyAiCount)
            {
                lobbyAiColors.RemoveRange(count, lobbyAiCount - count);
                lobbyAiDifficulties.RemoveRange(count, lobbyAiCount - count);
            }
            while (lobbyAiCount < count)
            {
                lobbyAiColors.Add(LobbyColors.FirstAvailable(lobby.Select(item => item.ColorId), lobbyAiColors));
                lobbyAiDifficulties.Add(AiDifficulty.Normal);
            }
            PublishLobby();
            return;
        }
        if (kind == PacketKind.LobbyAiDifficulty && M5Protocol.TryReadLobbyAiDifficulty(reader, out int aiIndex, out AiDifficulty difficulty))
        {
            if (index != 0) { LobbyFailure(peer, LobbyError.NotHost); return; }
            if (aiIndex >= lobbyAiCount) { LobbyFailure(peer, LobbyError.InvalidState); return; }
            lobbyAiDifficulties[aiIndex] = difficulty;
            PublishLobby();
            return;
        }
        if (kind != PacketKind.LobbyStart || !M5Protocol.TryReadLobbyStart(reader))
        { LobbyFailure(peer, LobbyError.InvalidPacket); return; }
        if (index != 0) { LobbyFailure(peer, LobbyError.NotHost); return; }
        if (lobby.Count + lobbyAiCount < lanRoom.Minimum ||
            lobby.Any(item => !item.Ready || item.Peer.ConnectionState != LiteNetLib.ConnectionState.Connected))
        { LobbyFailure(peer, LobbyError.NotReady); return; }

        NetworkWorld candidate;
        try { candidate = new NetworkWorld(rules.ForRoom(lobby.Count + lobbyAiCount), settings); }
        catch (ArgumentException) { LobbyFailure(peer, LobbyError.SpawnFailed); return; }
        var owners = new int[lobby.Count];
        for (int memberIndex = 0; memberIndex < lobby.Count; memberIndex++)
            if (candidate.TryJoin(lobby[memberIndex].ChosenElement, out owners[memberIndex], lobby[memberIndex].Account.Account.Nickname, lobby[memberIndex].ColorId) != JoinFailure.None)
            { LobbyFailure(peer, LobbyError.SpawnFailed); return; }
        if (candidate.AddAiOpponents(lobbyAiCount, member.ChosenElement, lobbyAiDifficulties, lobbyAiColors) != JoinFailure.None)
        { LobbyFailure(peer, LobbyError.SpawnFailed); return; }
        Simulation = candidate;
        roomStarted = true;
        for (int memberIndex = 0; memberIndex < lobby.Count; memberIndex++)
        {
            LobbyPeer joined = lobby[memberIndex];
            accountOwners[joined.Account.Account.Id] = owners[memberIndex];
            Attach(joined.Peer, owners[memberIndex], joined.Account, joined.Bytes, false);
        }
        lobby.Clear();
    }

    private void PublishLobby()
    {
        if (lanRoom == null || roomStarted || lobby.Count == 0) return;
        var members = lobby.Select(member => new LobbyMember(member.Account.Account.Nickname, member.ChosenElement, member.Ready, member.ColorId)).ToArray();
        for (int index = 0; index < lobby.Count; index++)
        {
            M5Protocol.WriteLobbyState(writer, lanRoom.Title, index, lanRoom.Minimum, lanRoom.Capacity, members, lobbyAiCount, lobbyAiDifficulties, lobbyAiColors);
            lobby[index].Peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void LobbyFailure(NetPeer peer, LobbyError error)
    {
        M5Protocol.WriteLobbyError(writer, error);
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private void Attach(NetPeer peer, int owner, IssuedAccount account, int bytes, bool resumed)
    {
        var session = new Session(owner, peer, Simulation.Rules, Simulation.World.GetView(owner))
        { Resumed = resumed, AccountKey = account.Token.Length == 0 ? null : account.Token };
        session.Traffic.RecordReceived(bytes, session.Clock.Elapsed.TotalSeconds);
        sessions.Add(peer.Id, session);
        Welcome(session);
    }

    private void Welcome(Session session)
    {
        if (!Simulation.World.TryGetView(session.Owner, out var view)) { EndSession(session, SessionEndReason.BaseDestroyed); return; }
        try
        {
            Protocol.WriteWelcome(writer, session.Owner, session.Resumed, session.AccountKey, Simulation.Rules.RoomPlayers);
            Send(session, DeliveryMethod.ReliableOrdered);
            SendFog(session, view);
            SendGameplay(session, view);
        }
        catch (ReplicationOverflowException) { manager.DisconnectPeerForce(session.Peer); }
        finally
        {
            if (session.AccountKey != null) CryptographicOperations.ZeroMemory(session.AccountKey);
            session.AccountKey = null;
        }
    }

    private void DeliverChat(int owner, ChatMessage message)
    {
        foreach (Session session in sessions.Values)
        {
            if (session.Owner != owner) continue;
            try
            {
                M5Protocol.WriteChatMessage(writer, message);
                Send(session, DeliveryMethod.ReliableOrdered, M5Protocol.ChatChannel);
            }
            catch (ReplicationOverflowException) { manager.DisconnectPeerForce(session.Peer); }
            return;
        }
    }

    private void StartDiscovery()
    {
        var listener = new EventBasedNetListener();
        var output = new NetDataWriter();
        discovery = new NetManager(listener) { AutoRecycle = true, BroadcastReceiveEnabled = true, UnconnectedMessagesEnabled = true };
        listener.NetworkReceiveUnconnectedEvent += (endpoint, reader, _) =>
        {
            if (!hostJoined || RoomClosed || !IsLocal(endpoint.Address) || !M5Protocol.TryReadDiscovery(reader, out uint nonce)) return;
            long now = Environment.TickCount64;
            if (now < nextDiscoveryAt) return;
            nextDiscoveryAt = now + 30;
            int count = roomStarted ? sessions.Count + Simulation.LivingAiCount : lobby.Count + lobbyAiCount;
            var room = new LanRoomListing(Port, lanRoom!.Title, count, lanRoom.Minimum, lanRoom.Capacity, roomStarted);
            M5Protocol.WriteRoomListing(output, nonce, room);
            discovery.SendUnconnectedMessage(output, endpoint);
        };
        if (!discovery.Start(settings.DiscoveryPort)) throw new IOException("LAN 검색 포트를 열지 못했습니다.");
    }

    private static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254);
    }
}
