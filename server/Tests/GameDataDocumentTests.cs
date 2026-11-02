using System.Security.Cryptography;
using System.Text;
using ElementalLegion.Shared.Data;
using LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ElementalLegion.Tests;

public sealed class GameDataDocumentTests
{
    [Fact]
    public void SharedDataIsCopiedAndBothDependenciesAreUsable()
    {
        var data = new GameDataDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "gamedata.json")));
        Assert.NotEmpty(data.Root["elements"]!);
        var writer = new NetDataWriter();
        writer.Put(data.Sha256);
        Assert.Equal(data.Sha256, new NetDataReader(writer.CopyData()).GetString());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void HashUsesUtf8BytesWithLfLineEndings(string newline)
    {
        const string lf = "{\n  \"name\": \"원소\",\n  \"_comment\": \"설명\"\n}\n";
        var data = new GameDataDocument(lf.Replace("\n", newline));
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lf))).ToLowerInvariant();
        Assert.Equal(expected, data.Sha256);
        Assert.Equal("원소", data.Root["name"]!.Value<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyInputIsRejected(string? json)
    {
        Assert.Throws<ArgumentException>(() => new GameDataDocument(json!));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{broken")]
    public void InvalidDocumentIsRejected(string json)
    {
        Assert.Throws<JsonReaderException>(() => new GameDataDocument(json));
    }

    [Fact]
    public void DataChangesChangeTheHash()
    {
        Assert.NotEqual(new GameDataDocument("{\"value\":1}").Sha256, new GameDataDocument("{\"value\":2}").Sha256);
    }
}
