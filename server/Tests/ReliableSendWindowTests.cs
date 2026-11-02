using ElementalLegion.Server;
using ElementalLegion.Shared.Net;
using Xunit;

namespace ElementalLegion.Tests;

/// <summary>재전송 대기 상한을 실제 전달 완료와 명시적인 시간으로 검사한다.</summary>
public sealed class ReliableSendWindowTests
{
    [Fact]
    public void DeliveryReleasesBytesAndUnacknowledgedMessagesExpireAfterTwoSeconds()
    {
        var window=new ReliableSendWindow();object first=window.Record(100,0);object second=window.Record(200,0.2);
        Assert.Equal(300,window.PendingBytes);window.Delivered(second);Assert.Equal(100,window.PendingBytes);
        Assert.False(window.HasExpired(2));Assert.True(window.HasExpired(2.001));window.Delivered(first);
        Assert.Equal(0,window.PendingBytes);Assert.False(window.HasExpired(5));
        window.Record(GameplayCodec.MaxBytes,6);Assert.Throws<ReplicationOverflowException>(()=>window.Record(1,6));
        window.Clear();Assert.Equal(0,window.PendingBytes);
    }
}
