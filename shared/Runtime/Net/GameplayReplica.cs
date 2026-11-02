using System;
using System.Buffers;
using System.Collections.Generic;
using ElementalLegion.Shared.Sim;
using ElementalLegion.Shared.Data;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>신뢰 프레임을 원자 검증하고 시야 대기 중의 모든 결과를 제한된 큐로 보존한다.</summary>
    public sealed class GameplayReplica : IDisposable
    {
        private readonly struct QueuedFrame
        {
            public readonly byte[] Bytes;public readonly int Length,Owner;public readonly long Tick;public readonly uint Revision;public readonly double At;
            public QueuedFrame(byte[] bytes,int length,int owner,long tick,uint revision,double at){Bytes=bytes;Length=length;Owner=owner;Tick=tick;Revision=revision;At=at;}
        }
        private readonly GameRules rules;
        private readonly byte[] scratch=new byte[GameplayCodec.MaxBytes];
        private readonly bool[] received=new bool[(GameplayCodec.MaxBytes+GameplayCodec.PayloadBytes-1)/GameplayCodec.PayloadBytes];
        private readonly Queue<QueuedFrame> queue=new Queue<QueuedFrame>();
        private readonly NetDataReader bodyReader=new NetDataReader();
        private readonly Dictionary<ObservedEntityId,long> hidden=new Dictionary<ObservedEntityId,long>();
        private readonly List<ObservedEntityId> expired=new List<ObservedEntityId>();
        private GameplayFrame spare;
        private long assembling=-1,completed=-1;
        private int owner=-1,assemblingOwner,total,receivedCount,queuedBytes;
        private uint revision,batch;
        private double partialAt;
        public GameplayFrame State {get;private set;}
        public int PendingBytes=>queuedBytes+(assembling>=0?total:0);
        public bool Overflowed {get;private set;}
        public string FailureReason {get;private set;}=string.Empty;
        public GameplayReplica(GameRules rules){this.rules=rules??throw new ArgumentNullException(nameof(rules));State=new GameplayFrame(rules);spare=new GameplayFrame(rules);}
        public bool BindPlayer(int player){if(player<=0||player>rules.MaxPlayers||owner>0&&owner!=player||queue.Count>0&&queue.Peek().Owner!=player)return false;owner=player;return true;}
        public bool HasExpired(double now)=>Overflowed||queue.Count>0&&now-queue.Peek().At>2||assembling>=0&&now-partialAt>2;
        public bool TryAccept(NetDataReader reader,double now)
        {
            if(double.IsNaN(now)||double.IsInfinity(now)||now<0||reader.AvailableBytes<GameplayCodec.HeaderBytes||reader.AvailableBytes>Protocol.MaxDatagramBytes||reader.GetByte()!=(byte)PacketKind.Gameplay)return Fail("신뢰 관측 패킷의 길이·형식이 잘못되었습니다.");
            long tick=reader.GetLong();int player=reader.GetInt();uint fog=reader.GetUInt(),sequence=reader.GetUInt();int length=reader.GetInt(),offset=reader.GetInt();
            if(tick<=completed||tick<assembling)return false;
            if(tick<0||player<=0||player>rules.MaxPlayers||owner>0&&owner!=player||fog==0||sequence==0||length<=0||length>GameplayCodec.MaxBytes||offset<0||offset>=length||offset%GameplayCodec.PayloadBytes!=0||reader.AvailableBytes!=Math.Min(GameplayCodec.PayloadBytes,length-offset))return Fail("신뢰 관측 헤더가 올바르지 않습니다.");
            if(tick!=assembling)
            {
                if(assembling>=0||queuedBytes>GameplayCodec.MaxBytes-length)return Fail("신뢰 결과가 미완성 상태에서 교체되거나 1MiB 대기를 초과했습니다.");
                assembling=tick;assemblingOwner=player;revision=fog;batch=sequence;total=length;receivedCount=0;partialAt=now;Array.Clear(received,0,received.Length);
            }
            if(player!=assemblingOwner||fog!=revision||sequence!=batch||length!=total)return Fail("신뢰 관측 조각의 revision·소유자가 다릅니다.");
            int part=offset/GameplayCodec.PayloadBytes,count=reader.AvailableBytes;
            for(int index=0;index<count;index++)
            {byte value=reader.GetByte();if(received[part]){if(scratch[offset+index]!=value)return Fail("신뢰 중복 조각의 내용이 다릅니다.");}else scratch[offset+index]=value;}
            if(!received[part]){received[part]=true;receivedCount++;}
            if(receivedCount!=(total+GameplayCodec.PayloadBytes-1)/GameplayCodec.PayloadBytes)return false;
            bodyReader.SetSource(scratch,0,total);
            if(!GameplayCodec.ReadBody(bodyReader,rules,spare,tick,player,fog)){assembling=-1;return Fail("완성된 신뢰 관측의 검증에 실패했습니다.");}
            if(queuedBytes+total>GameplayCodec.MaxBytes){Overflowed=true;assembling=-1;return false;}
            byte[] bytes=ArrayPool<byte>.Shared.Rent(total);Array.Copy(scratch,bytes,total);
            queue.Enqueue(new QueuedFrame(bytes,total,player,tick,fog,now));queuedBytes+=total;completed=tick;assembling=-1;return true;
        }
        public bool TryPublish(FogStateReplica fog,out GameplayFrame frame)
        {
            frame=null;if(!fog.Ready||queue.Count==0||queue.Peek().Revision>fog.Revision)return false;
            QueuedFrame next=queue.Dequeue();queuedBytes-=next.Length;
            try
            {
                if(next.Owner!=fog.PlayerId||owner>0&&next.Owner!=owner)return false;
                bodyReader.SetSource(next.Bytes,0,next.Length);
                if(!GameplayCodec.ReadBody(bodyReader,rules,spare,next.Tick,next.Owner,next.Revision))return false;
                spare.BuildingCount=Filter(spare.BuildingBuffer,spare.BuildingCount,ObservedEntityKind.Building,next.Tick);
                spare.CoreCount=Filter(spare.CoreBuffer,spare.CoreCount,ObservedEntityKind.Core,next.Tick);
                spare.AreaCount=Filter(spare.AreaBuffer,spare.AreaCount,ObservedEntityKind.Area,next.Tick);
                expired.Clear();foreach(var item in hidden)if(item.Value<next.Tick)expired.Add(item.Key);
                foreach(ObservedEntityId item in expired)hidden.Remove(item);
                spare.SpatialReady=next.Revision==fog.Revision;GameplayFrame previous=State;State=spare;spare=previous;frame=State;return true;
            }
            finally {ArrayPool<byte>.Shared.Return(next.Bytes);}
        }
        public void Forget(ObservedEntityId entity,long tick)
        {
            if(entity.Id<=0||entity.Kind>ObservedEntityKind.Area||tick<0)throw new ArgumentOutOfRangeException(nameof(entity));
            if(State.Tick>tick||hidden.TryGetValue(entity,out long previous)&&previous>=tick)return;hidden[entity]=tick;
            State.BuildingCount=Filter(State.BuildingBuffer,State.BuildingCount,ObservedEntityKind.Building,State.Tick);
            State.CoreCount=Filter(State.CoreBuffer,State.CoreCount,ObservedEntityKind.Core,State.Tick);
            State.AreaCount=Filter(State.AreaBuffer,State.AreaCount,ObservedEntityKind.Area,State.Tick);
        }
        private int Filter<T>(T[] values,int count,ObservedEntityKind kind,long tick) where T:struct
        {
            int write=0;for(int index=0;index<count;index++)
            {
                // 이 세 관측 구조체는 종류별로 분리되어 있다. boxing 없는 ID 조회를 사용한다.
                int id=kind==ObservedEntityKind.Building?((BuildingView[])(object)values)[index].Id:kind==ObservedEntityKind.Core?((CoreView[])(object)values)[index].Id:((AreaView[])(object)values)[index].Id;
                if(!hidden.TryGetValue(new ObservedEntityId(kind,id),out long at)||tick>at)values[write++]=values[index];
            }
            return write;
        }
        public void Reset()
        {
            while(queue.Count>0)ArrayPool<byte>.Shared.Return(queue.Dequeue().Bytes);
            State.Clear();spare.Clear();owner=-1;assembling=completed=-1;queuedBytes=receivedCount=total=0;Overflowed=false;
            hidden.Clear();expired.Clear();
            Array.Clear(received,0,received.Length);
            FailureReason=string.Empty;
        }
        public void Dispose()=>Reset();
        private bool Fail(string reason){Overflowed=true;FailureReason=reason;return false;}
    }
}
