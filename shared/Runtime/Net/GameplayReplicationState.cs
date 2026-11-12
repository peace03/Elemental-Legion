using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>매 틱의 허용 사건을 보존하고 전송 때 다시 시야로 거른다. Prepare→전송→Commit 순서다.</summary>
    public sealed class GameplayReplicationState
    {
        private readonly List<NetworkEvent> journal=new List<NetworkEvent>(256);
        private readonly NetworkEvent[] outgoing=new NetworkEvent[GameplayCodec.MaxEvents];
        private readonly HashSet<int> conversions=new HashSet<int>();
        private readonly HashSet<ObservedEntityId> visibleIds=new HashSet<ObservedEntityId>();
        private readonly NetDataWriter body=new NetDataWriter();
        private long captured=-1,tick;
        private int owner;
        private uint revision,batch;
        private ulong serial;
        private bool prepared;
        public int ChunkCount=>(body.Length+GameplayCodec.PayloadBytes-1)/GameplayCodec.PayloadBytes;
        public GameplayReplicationState(GameRules rules) {if(rules==null)throw new ArgumentNullException(nameof(rules));}
        public void Capture(WorldView view)
        {
            Bind(view);if(view.Tick<=captured)return;captured=view.Tick;conversions.Clear();
            foreach(SimEvent item in view.Events)if((item.Kind==SimEventKind.Converted||item.Kind==SimEventKind.Rescued)&&item.EntityId>0)conversions.Add(item.EntityId);
            foreach(SimEvent item in view.Events)
            {
                if(item.Kind==SimEventKind.Death&&item.EntityKind==ObservedEntityKind.Unit&&conversions.Contains(item.EntityId))continue;
                if((item.Kind==SimEventKind.Death||item.Kind==SimEventKind.Converted||item.Kind==SimEventKind.Rescued)&&item.EntityId==0)continue;
                if(journal.Count==GameplayCodec.MaxEvents)throw new ReplicationOverflowException();
                journal.Add(new NetworkEvent(checked(++serial),view.Tick,item));
            }
        }
        public void Prepare(WorldView view,uint fogRevision)
        {
            Bind(view);if(prepared||fogRevision==0)throw new InvalidOperationException("시야 기준 및 이전 프레임 Commit을 확인하세요.");
            visibleIds.Clear();
            foreach(UnitView unit in view.Units)visibleIds.Add(new ObservedEntityId(ObservedEntityKind.Unit,unit.Id));
            foreach(BuildingView building in view.Buildings)visibleIds.Add(new ObservedEntityId(ObservedEntityKind.Building,building.Id));
            int count=0;
            foreach(NetworkEvent item in journal)
            {
                SimEvent data=item.Data;
                bool alert=data.Kind==SimEventKind.BaseUnderAttack;
                bool command=data.Kind==SimEventKind.CommandRejected||data.Kind==SimEventKind.CommandApplied;
                bool personal=command||alert;
                if (personal ? data.OwnerId != view.PlayerId : !view.CanSeePoint(data.Position))
                {
                    // 시야 밖 핵 획득은 소유자에게 수량만 알려 준다.
                    if (data.Kind == SimEventKind.CorePickedUp && data.OwnerId == view.PlayerId)
                    {
                        var receipt = new SimEvent(data.Kind, data.OwnerId, 0, 0, Vector2.Zero, data.Amount, entityKind: ObservedEntityKind.Core);
                        outgoing[count++] = new NetworkEvent(item.Serial, item.Tick, receipt);
                    }
                    continue;
                }
                bool origin=!alert&&data.HasSourcePosition&&Visible(data.SourceKind,data.SourceId)&&view.CanSeePoint(data.SourcePosition);
                // 포집기의 위치는 현재 관측된 건물에서 얻으므로 사건에는 ID만 보낸다.
                bool source=origin||data.Kind==SimEventKind.CorePickedUp&&data.SourceKind==ObservedEntityKind.Building&&Visible(data.SourceKind,data.SourceId);
                bool ownerKnown=source||data.OwnerId==view.PlayerId||data.Kind==SimEventKind.Converted||data.Kind==SimEventKind.Rescued||data.Kind==SimEventKind.Trained;
                bool victim=data.Kind==SimEventKind.Death||data.Kind==SimEventKind.Converted||data.Kind==SimEventKind.Rescued||data.Kind==SimEventKind.Trained;
                bool target=!alert&&(victim&&data.EntityKind==ObservedEntityKind.Unit&&data.EntityId>0||Visible(data.TargetKind,data.TargetId));
                int entity=alert?0:data.EntityId;
                if(data.EntityKind==ObservedEntityKind.Unit&&!target||data.EntityKind==ObservedEntityKind.Building&&!Visible(data.EntityKind,entity))entity=0;
                var sanitized=new SimEvent(data.Kind,ownerKnown?data.OwnerId:-1,source?data.SourceId:0,target?data.TargetId:0,command?Vector2.Zero:data.Position,data.Amount,alert?0:data.Detail,alert?0:data.Sequence,
                    origin||victim?data.Element:-1,origin?data.SourcePosition:Vector2.Zero,origin,entity,data.EntityKind,
                    source?data.SourceKind:ObservedEntityKind.Unit,target&&data.TargetId>0?data.TargetKind:ObservedEntityKind.Unit);
                outgoing[count++]=new NetworkEvent(item.Serial,item.Tick,sanitized);
            }
            GameplayCodec.WriteBody(body,view,outgoing,count);tick=view.Tick;revision=fogRevision;batch=checked(batch+1);prepared=true;
        }
        public void WriteChunk(NetDataWriter writer,int part)
        {if(!prepared)throw new InvalidOperationException("Prepare가 필요합니다.");GameplayCodec.WriteChunk(writer,body,tick,owner,revision,batch,part);}
        public void Commit(){if(!prepared)throw new InvalidOperationException("준비한 전송이 없습니다.");journal.Clear();prepared=false;}
        private void Bind(WorldView view)
        {if(view.PlayerId<=0||owner!=0&&owner!=view.PlayerId)throw new ArgumentException("다른 수신자의 관측입니다.");owner=view.PlayerId;}
        private bool Visible(ObservedEntityKind kind,int id)=>id>0&&visibleIds.Contains(new ObservedEntityId(kind,id));
    }
}
