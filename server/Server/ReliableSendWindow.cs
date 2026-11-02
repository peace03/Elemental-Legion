using ElementalLegion.Shared.Net;

namespace ElementalLegion.Server;

/// <summary>LiteNetLib 전달 완료 태그를 재사용하며 실제 미확인 bytes와 가장 오래된 전송을 제한한다.</summary>
public sealed class ReliableSendWindow
{
    private sealed class Receipt
    {
        public ReliableSendWindow Owner=null!;
        public int Bytes;public double At;public bool Complete;
    }
    private readonly Queue<Receipt> pending=new();
    private readonly Stack<Receipt> pool=new();
    private bool closed;
    public int PendingBytes {get;private set;}
    public bool HasExpired(double now)=>pending.Count>0&&now-pending.Peek().At>2;
    public object Record(int bytes,double now)
    {
        if(bytes<0||!double.IsFinite(now)||now<0)throw new ArgumentOutOfRangeException(nameof(bytes));
        if(closed||PendingBytes>GameplayCodec.MaxBytes-bytes||HasExpired(now))throw new ReplicationOverflowException();
        Receipt receipt=pool.Count>0?pool.Pop():new Receipt();receipt.Owner=this;receipt.Bytes=bytes;receipt.At=now;receipt.Complete=false;
        pending.Enqueue(receipt);PendingBytes+=bytes;return receipt;
    }
    public void Delivered(object tag)
    {
        if(closed||tag is not Receipt receipt||receipt.Owner!=this||receipt.Complete)return;
        receipt.Complete=true;PendingBytes-=receipt.Bytes;
        while(pending.Count>0&&pending.Peek().Complete)pool.Push(pending.Dequeue());
    }
    public void Clear(){closed=true;PendingBytes=0;pending.Clear();pool.Clear();}
}
