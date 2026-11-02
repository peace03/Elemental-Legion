using System;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>자기 상태와 허용된 정적 관측·사건의 v3 바이너리 계약이다.</summary>
    public static class GameplayCodec
    {
        public const int MaxBytes=1024*1024,MaxObjects=4096,MaxBuildings=256,MaxEvents=8192;
        public const int HeaderBytes=29,PayloadBytes=Protocol.MaxDatagramBytes-HeaderBytes;
        private const int EventBytes=69;
        internal static void WriteBody(NetDataWriter writer,WorldView view,NetworkEvent[] events,int count)
        {
            writer.Reset();PutPoint(writer,view.BasePosition);writer.Put(view.MainElement);writer.Put(view.BaseLevel);writer.Put(view.CoreBalance);
            writer.Put(view.Population);writer.Put(view.ReservedPopulation);writer.Put(view.PopulationCap);writer.Put(view.GarrisonCount);
            writer.Put(view.TrainingElement);writer.Put(view.TrainingStartTick);writer.Put(view.TrainingEndTick);writer.Put(view.TrainedCount);writer.Put(view.ConvertedCount);
            foreach(int value in view.ElementCounts)writer.Put(value);
            if(view.Buildings.Count>MaxBuildings||view.Cores.Count>MaxObjects||view.Areas.Count>MaxObjects||count>MaxEvents)throw new ReplicationOverflowException();
            writer.Put(view.Buildings.Count);foreach(BuildingView building in view.Buildings)
            {writer.Put(building.Id);writer.Put(building.OwnerId);writer.Put((byte)building.Kind);PutPoint(writer,building.Position);writer.Put(building.Radius);}
            writer.Put(view.Cores.Count);foreach(CoreView core in view.Cores)
            {writer.Put(core.Id);writer.Put(core.Amount);PutPoint(writer,core.Position);writer.Put(core.EndTick);}
            writer.Put(view.Areas.Count);foreach(AreaView area in view.Areas)
            {writer.Put(area.Id);writer.Put((byte)area.Kind);PutPoint(writer,area.Position);writer.Put(area.Radius);writer.Put(area.EndTick);}
            writer.Put(count);
            for(int index=0;index<count;index++)
            {
                NetworkEvent item=events[index];SimEvent data=item.Data;
                writer.Put((byte)data.Kind);writer.Put(data.OwnerId);writer.Put(data.SourceId);writer.Put(data.TargetId);PutPoint(writer,data.Position);
                writer.Put(data.Amount);writer.Put(data.Detail);writer.Put(data.Sequence);writer.Put((short)data.Element);writer.Put((byte)(data.HasSourcePosition?1:0));
                PutPoint(writer,data.SourcePosition);writer.Put((byte)data.EntityKind);writer.Put(data.EntityId);writer.Put(item.Tick);writer.Put(item.Serial);
            }
            if(writer.Length>MaxBytes)throw new ReplicationOverflowException();
        }

        internal static void WriteChunk(NetDataWriter output,NetDataWriter body,long tick,int owner,uint revision,uint batch,int part)
        {
            int offset=checked(part*PayloadBytes);if(offset<0||offset>=body.Length)throw new ArgumentOutOfRangeException(nameof(part));
            output.Reset();output.Put((byte)PacketKind.Gameplay);output.Put(tick);output.Put(owner);output.Put(revision);output.Put(batch);output.Put(body.Length);output.Put(offset);
            output.Put(body.Data,offset,Math.Min(PayloadBytes,body.Length-offset));
        }

        internal static bool ReadBody(NetDataReader reader,GameRules rules,GameplayFrame frame,long tick,int owner,uint revision)
        {
            if(reader.AvailableBytes<76+rules.Elements.Count*4+16)return false;
            frame.Tick=tick;frame.PlayerId=owner;frame.Revision=revision;frame.BasePosition=Point(reader);frame.MainElement=reader.GetInt();frame.BaseLevel=reader.GetInt();frame.CoreBalance=reader.GetLong();
            frame.Population=reader.GetInt();frame.ReservedPopulation=reader.GetInt();frame.PopulationCap=reader.GetInt();frame.GarrisonCount=reader.GetInt();
            frame.TrainingElement=reader.GetInt();frame.TrainingStartTick=reader.GetLong();frame.TrainingEndTick=reader.GetLong();frame.TrainedCount=reader.GetLong();frame.ConvertedCount=reader.GetLong();
            if(!ValidPoint(frame.BasePosition,rules)||!rules.StartingElements.Contains(frame.MainElement)||frame.BaseLevel<1||frame.BaseLevel>rules.MaxBaseLevel||frame.CoreBalance<0||frame.Population<0||frame.Population>rules.MaxPopulation||frame.PopulationCap<frame.Population||frame.PopulationCap>rules.MaxPopulation||frame.GarrisonCount<0||frame.GarrisonCount>frame.Population||frame.TrainedCount<0||frame.ConvertedCount<0||frame.TrainedCount>long.MaxValue-frame.ConvertedCount)return false;
            if(frame.TrainingElement< -1||frame.TrainingElement>=rules.Elements.Count||frame.ReservedPopulation!=(frame.TrainingElement>=0?1:0)||frame.Population+frame.ReservedPopulation>frame.PopulationCap)return false;
            if(frame.TrainingElement>=0 ? frame.TrainingStartTick<0||frame.TrainingStartTick>tick||frame.TrainingEndTick<tick : frame.TrainingStartTick!=0||frame.TrainingEndTick!=0)return false;
            int population=0;for(int index=0;index<frame.Composition.Length;index++)
            {int value=reader.GetInt();if(value<0||value>rules.MaxPopulation)return false;frame.Composition[index]=value;population+=value;}
            if(population!=frame.Population)return false;
            if(!Count(reader,MaxBuildings,21,out frame.BuildingCount))return false;int previous=0;
            for(int index=0;index<frame.BuildingCount;index++)
            {
                int id=reader.GetInt(),player=reader.GetInt();BuildingKind kind=(BuildingKind)reader.GetByte();Vector2 position=Point(reader);float radius=reader.GetFloat();
                if(id<=previous||player<=0||player>rules.MaxPlayers||kind>BuildingKind.EnergyForge||!ValidPoint(position,rules)||!Protocol.Finite(radius)||radius<=0||radius>rules.MapSize)return false;
                previous=id;frame.BuildingBuffer[index]=new BuildingView(id,player,kind,position,radius);
            }
            if(!Count(reader,MaxObjects,24,out frame.CoreCount))return false;previous=0;
            for(int index=0;index<frame.CoreCount;index++)
            {
                int id=reader.GetInt(),amount=reader.GetInt();Vector2 position=Point(reader);long end=reader.GetLong();
                if(id<=previous||amount<=0||!ValidPoint(position,rules)||end<tick)return false;
                previous=id;frame.CoreBuffer[index]=new CoreView(id,amount,position,end);
            }
            if(!Count(reader,MaxObjects,25,out frame.AreaCount))return false;previous=0;
            for(int index=0;index<frame.AreaCount;index++)
            {
                int id=reader.GetInt();ReactionKind kind=(ReactionKind)reader.GetByte();Vector2 position=Point(reader);float radius=reader.GetFloat();long end=reader.GetLong();
                if(id<=previous||kind>ReactionKind.PlasmaBurst||!ValidPoint(position,rules)||!Protocol.Finite(radius)||radius<=0||radius>rules.MapSize||end<tick)return false;
                previous=id;frame.AreaBuffer[index]=new AreaView(id,kind,position,radius,end);
            }
            if(!Count(reader,MaxEvents,EventBytes,out frame.EventCount)||reader.AvailableBytes!=frame.EventCount*EventBytes)return false;
            ulong previousSerial=0;long previousTick=-1;
            for(int index=0;index<frame.EventCount;index++)
            {
                SimEventKind kind=(SimEventKind)reader.GetByte();int player=reader.GetInt(),source=reader.GetInt(),target=reader.GetInt();Vector2 position=Point(reader);
                float amount=reader.GetFloat();int detail=reader.GetInt();ulong sequence=reader.GetULong();int element=reader.GetShort();byte hasOrigin=reader.GetByte();
                Vector2 origin=Point(reader);ObservedEntityKind entityKind=(ObservedEntityKind)reader.GetByte();int entity=reader.GetInt();long at=reader.GetLong();ulong serial=reader.GetULong();
                if(kind>SimEventKind.Impact||player< -1||player>rules.MaxPlayers||player==0||source<0||target<0||!ValidPoint(position,rules)||!Protocol.Finite(amount)||amount<0||element< -1||element>=rules.Elements.Count||hasOrigin>1||entityKind>ObservedEntityKind.Area||entity<0||at<previousTick||at<0||at>tick||serial<=previousSerial)return false;
                if(hasOrigin==0 ? origin!=Vector2.Zero : source==0||element<0||!ValidPoint(origin,rules))return false;
                if(kind==SimEventKind.Reaction&&(detail<0||detail>(int)ReactionKind.PlasmaBurst)||kind==SimEventKind.CommandRejected&&(player!=owner||sequence==0||detail<=0||detail>(int)CommandFailure.NotUnlocked))return false;
                frame.EventBuffer[index]=new NetworkEvent(serial,at,new SimEvent(kind,player,source,target,position,amount,detail,sequence,element,origin,hasOrigin!=0,entity,entityKind));
                previousSerial=serial;previousTick=at;
            }
            return true;
        }
        private static bool Count(NetDataReader reader,int maximum,int bytes,out int count)
        {count=0;if(reader.AvailableBytes<4)return false;count=reader.GetInt();return count>=0&&count<=maximum&&reader.AvailableBytes>=count*bytes;}
        private static void PutPoint(NetDataWriter writer,Vector2 point){writer.Put(point.X);writer.Put(point.Y);}
        private static Vector2 Point(NetDataReader reader)=>new Vector2(reader.GetFloat(),reader.GetFloat());
        private static bool ValidPoint(Vector2 point,GameRules rules)=>Protocol.Finite(point)&&point.X>=0&&point.Y>=0&&point.X<=rules.MapSize&&point.Y<=rules.MapSize;
    }

    /// <summary>관측 전송의 기술 상한을 넘으면 결과를 유실시키지 않고 연결을 종료하도록 알린다.</summary>
    public sealed class ReplicationOverflowException : InvalidOperationException
    {public ReplicationOverflowException():base("클라이언트 관측 대기 상한을 넘었습니다.") { }}
}
