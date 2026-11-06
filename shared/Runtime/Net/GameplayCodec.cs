using System;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Shared.Net
{
    /// <summary>자기 상태와 허용된 정적 관측·사건의 바이너리 계약이다.</summary>
    public static class GameplayCodec
    {
        public const int MaxBytes=1024*1024,MaxObjects=4096,MaxBuildings=1024,MaxEvents=8192;
        public const int HeaderBytes=29,PayloadBytes=Protocol.MaxDatagramBytes-HeaderBytes;
        private const int BuildingBytes=46, EventBytes=71, OwnStateBytes=100, StandingHeaderBytes=13, TrainingOrderBytes=9;
        internal static void WriteBody(NetDataWriter writer,WorldView view,NetworkEvent[] events,int count)
        {
            writer.Reset();PutPoint(writer,view.BasePosition);writer.Put(view.MainElement);writer.Put(view.BaseLevel);writer.Put(view.CoreBalance);
            writer.Put(view.Population);writer.Put(view.ReservedPopulation);writer.Put(view.PopulationCap);writer.Put(view.GarrisonCount);
            writer.Put(view.RallyingCount);
            writer.Put(view.TrainingElement);writer.Put(view.TrainingStartTick);writer.Put(view.TrainingEndTick);writer.Put(view.TrainedCount);writer.Put(view.ConvertedCount);
            writer.Put(view.GarrisonArrivals);
            writer.Put(view.OwnBuildingCount); writer.Put(view.BattleWins);
            foreach (TrainingOrderView order in view.TrainingQueue)
            {
                writer.Put(order.Id);
                writer.Put((byte)order.Element);
            }
            foreach(int value in view.ElementCounts)writer.Put(value);
            writer.Put(view.Standings.Count);
            foreach (PlayerStanding standing in view.Standings)
            {
                writer.Put(standing.PlayerId);
                writer.Put(standing.Score);
                writer.Put((byte)standing.ColorId);
                writer.Put(standing.Nickname);
            }
            writer.Put(view.OwnerColors.Count);
            foreach (OwnerColorView ownerColor in view.OwnerColors) { writer.Put(ownerColor.PlayerId); writer.Put((byte)ownerColor.ColorId); }
            if(view.Buildings.Count>MaxBuildings||view.Cores.Count>MaxObjects||view.Areas.Count>MaxObjects||count>MaxEvents)throw new ReplicationOverflowException();
            writer.Put(view.Buildings.Count);foreach(BuildingView building in view.Buildings)
            {
                writer.Put(building.Id);writer.Put(building.OwnerId);writer.Put((byte)building.Kind);PutPoint(writer,building.Position);writer.Put(building.Radius);
                writer.Put(building.Health);writer.Put(building.MaxHealth);
                writer.Put(building.TowerElement < 0 ? Protocol.NoTowerElement : (byte)building.TowerElement);
                writer.Put(building.GhostEndTick);
                writer.Put(building.ConstructionEndTick);
            }
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
                writer.Put((byte)data.SourceKind);writer.Put((byte)data.TargetKind);
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
            if(reader.AvailableBytes<OwnStateBytes+rules.Elements.Count*4+24)return false;
            frame.Tick=tick;frame.PlayerId=owner;frame.Revision=revision;frame.BasePosition=Point(reader);frame.MainElement=reader.GetInt();frame.BaseLevel=reader.GetInt();frame.CoreBalance=reader.GetLong();
            frame.Population=reader.GetInt();frame.ReservedPopulation=reader.GetInt();frame.PopulationCap=reader.GetInt();frame.GarrisonCount=reader.GetInt();
            frame.RallyingCount = reader.GetInt();
            if (frame.RallyingCount < 0 || frame.RallyingCount > frame.GarrisonCount) return false;
            frame.TrainingElement=reader.GetInt();frame.TrainingStartTick=reader.GetLong();frame.TrainingEndTick=reader.GetLong();frame.TrainedCount=reader.GetLong();frame.ConvertedCount=reader.GetLong();
            frame.GarrisonArrivals = reader.GetLong();
            frame.OwnBuildingCount = reader.GetInt();
            frame.BattleWins = reader.GetLong();
            if (frame.GarrisonArrivals < 0 || frame.OwnBuildingCount < 0 || frame.OwnBuildingCount > MaxBuildings ||
                frame.BattleWins < 0 || frame.BattleWins > int.MaxValue) return false;
            if(!ValidPoint(frame.BasePosition,rules)||!rules.StartingElements.Contains(frame.MainElement)||frame.BaseLevel<1||frame.BaseLevel>rules.MaxBaseLevel||frame.CoreBalance<0||frame.Population<0||frame.Population>rules.MaxPopulation||frame.PopulationCap<frame.Population||frame.PopulationCap>rules.MaxPopulation||frame.GarrisonCount<0||frame.GarrisonCount>frame.Population||frame.TrainedCount<0||frame.ConvertedCount<0||frame.TrainedCount>long.MaxValue-frame.ConvertedCount)return false;
            if (frame.TrainingElement < -1 || frame.TrainingElement >= rules.Elements.Count || frame.ReservedPopulation < 0 ||
                frame.ReservedPopulation > frame.PopulationCap - frame.Population ||
                (frame.TrainingElement >= 0) != (frame.ReservedPopulation > 0)) return false;
            if(frame.TrainingElement>=0 ? frame.TrainingStartTick<0||frame.TrainingStartTick>tick||frame.TrainingEndTick<tick : frame.TrainingStartTick!=0||frame.TrainingEndTick!=0)return false;
            int waiting = Math.Max(0, frame.ReservedPopulation - 1);
            int available = reader.AvailableBytes - rules.Elements.Count * 4 - 24;
            if (available < 0 || waiting > available / TrainingOrderBytes) return false;
            frame.TrainingQueueBuffer.Clear();
            ulong previousOrder = 0;
            for (int index = 0; index < waiting; index++)
            {
                ulong orderId = reader.GetULong();
                int element = reader.GetByte();
                if (orderId <= previousOrder || element >= rules.Elements.Count) return false;
                frame.TrainingQueueBuffer.Add(new TrainingOrderView(orderId, element));
                previousOrder = orderId;
            }
            int population=0;for(int index=0;index<frame.Composition.Length;index++)
            {int value=reader.GetInt();if(value<0||value>rules.MaxPopulation)return false;frame.Composition[index]=value;population+=value;}
            if(population!=frame.Population)return false;
            if (!ReadStandings(reader, rules, frame)) return false;
            if (!Count(reader, rules.MaxPlayers, 5, out int ownerColorCount)) return false;
            frame.OwnerColorBuffer.Clear();
            int previousOwner = -1, usedColors = 0;
            for (int index = 0; index < ownerColorCount; index++)
            {
                int id = reader.GetInt(), color = reader.GetByte();
                if (id <= previousOwner || id < 0 || !PlayerPalette.IsValid(color) || (usedColors & (1 << color)) != 0) return false;
                frame.OwnerColorBuffer.Add(new OwnerColorView(id, color));
                previousOwner = id; usedColors |= 1 << color;
            }
            foreach (PlayerStanding standing in frame.StandingBuffer)
            {
                bool found = false;
                foreach (OwnerColorView color in frame.OwnerColorBuffer)
                    if (color.PlayerId == standing.PlayerId && color.ColorId == standing.ColorId) found = true;
                if (!found) return false;
            }
            if(!Count(reader,MaxBuildings,BuildingBytes,out frame.BuildingCount))return false;int previous=0;
            for(int index=0;index<frame.BuildingCount;index++)
            {
                int id=reader.GetInt(),player=reader.GetInt();BuildingKind kind=(BuildingKind)reader.GetByte();Vector2 position=Point(reader);float radius=reader.GetFloat();
                float health=reader.GetFloat(),maxHealth=reader.GetFloat();int element=reader.GetByte();long ghostEnd=reader.GetLong();
                long constructionEnd = reader.GetLong();
                if(id<=previous||player<=0||kind>BuildingKind.Dome||!ValidPoint(position,rules)||!Protocol.Finite(radius)||radius<=0||radius>rules.MapSize||
                    !Protocol.Finite(health)||!Protocol.Finite(maxHealth)||health<=0||health>maxHealth||maxHealth!=rules.BuildingHealth(kind)||
                    ghostEnd!=0&&ghostEnd<=tick)return false;
                if (constructionEnd != 0 && (constructionEnd <= tick || ghostEnd != 0 ||
                    constructionEnd - tick > rules.BuildingConstructionTicks(kind))) return false;
                if (element == Protocol.NoTowerElement) element = -1;
                if (!ConstructionSystem.ValidTowerElement(rules, kind, element)) return false;
                previous=id;frame.BuildingBuffer[index]=new BuildingView(id,player,kind,position,radius,health,maxHealth,element,ghostEnd,constructionEnd);
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
                ObservedEntityKind sourceKind=(ObservedEntityKind)reader.GetByte(),targetKind=(ObservedEntityKind)reader.GetByte();
                if(kind>SimEventKind.CommandApplied||player< -1||player==0||source<0||target<0||!ValidPoint(position,rules)||!Protocol.Finite(amount)||amount<0||element< -1||element>=rules.Elements.Count||hasOrigin>1||entityKind>ObservedEntityKind.Area||entity<0||at<previousTick||at<0||at>tick||serial<=previousSerial)return false;
                if(sourceKind!=ObservedEntityKind.Unit&&sourceKind!=ObservedEntityKind.Building||
                    targetKind!=ObservedEntityKind.Unit&&targetKind!=ObservedEntityKind.Building||
                    source==0&&sourceKind!=ObservedEntityKind.Unit||target==0&&targetKind!=ObservedEntityKind.Unit)return false;
                if(hasOrigin==0 ? origin!=Vector2.Zero : source==0||element<0||!ValidPoint(origin,rules))return false;
                if(kind==SimEventKind.Reaction&&(detail<0||detail>(int)ReactionKind.PlasmaBurst)||
                    kind==SimEventKind.CommandRejected&&(player!=owner||sequence==0||detail<=0||detail>(int)CommandFailure.TrainingNotWaiting)||
                    kind==SimEventKind.CommandApplied&&(player!=owner||sequence==0||
                        detail!=(int)CommandKind.Build&&detail!=(int)CommandKind.UpgradeBase&&detail!=(int)CommandKind.CancelTraining))return false;
                frame.EventBuffer[index]=new NetworkEvent(serial,at,new SimEvent(kind,player,source,target,position,amount,detail,sequence,element,origin,hasOrigin!=0,entity,entityKind,sourceKind,targetKind));
                previousSerial=serial;previousTick=at;
            }
            return true;
        }
        private static bool ReadStandings(NetDataReader reader, GameRules rules, GameplayFrame frame)
        {
            if (!Count(reader, rules.MaxPlayers, StandingHeaderBytes + sizeof(ushort) + 1, out int count)) return false;
            frame.StandingBuffer.Clear();
            int lastId = 0;
            long lastScore = long.MaxValue;
            for (int index = 0; index < count; index++)
            {
                if (reader.AvailableBytes < StandingHeaderBytes) return false;
                int player = reader.GetInt();
                long score = reader.GetLong();
                int colorId = reader.GetByte();
                if (!PlayerPalette.IsValid(colorId)) return false;
                if (player <= 0 || score < 0 || score > lastScore || score == lastScore && player <= lastId) return false;
                if (!Protocol.TryReadNickname(reader, out string nickname)) return false;
                foreach (PlayerStanding entry in frame.StandingBuffer)
                    if (entry.PlayerId == player || entry.ColorId == colorId) return false;
                frame.StandingBuffer.Add(new PlayerStanding(player, score, nickname, colorId));
                lastId = player;
                lastScore = score;
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
