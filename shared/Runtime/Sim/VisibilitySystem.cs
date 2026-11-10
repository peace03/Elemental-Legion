using System;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>원형 시야와 군단 카메라 시야를 합치고 허용된 값만 재사용 버퍼에 담는다.</summary>
    internal static class VisibilitySystem
    {
        internal static void Refresh(WorldState world)
        {
            world.Grid.Rebuild(world.Units);
            Span<int> joined = stackalloc int[world.Rules.MaxPlayers * 4];
            Span<int> reinforcing = stackalloc int[joined.Length];
            SquadSystem.CountMembers(world, joined, reinforcing);
            foreach (UnitState unit in world.Units) unit.VisibleTo = 0;
            foreach (PlayerState player in world.Players) player.VisionSources.Clear();
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId >= 0 && unit.Health > 0) world.PlayerById[unit.OwnerId].VisionSources.Add(unit.Position);
            foreach (BuildingState building in world.Buildings)
                if (!building.IsUnderConstruction) world.PlayerById[building.OwnerId].VisionSources.Add(building.Position);
            world.Standings.Clear();
            foreach (PlayerState player in world.Players)
                if (player.GhostEndTick == 0 && player.Id > 0)
                    world.Standings.Add(new PlayerStanding(player.Id,
                        world.Rules.PlayerScore(player.BuildingCount, player.BaseLevel, player.Population, player.BattleWins), player.Nickname, player.ColorId));
            world.Standings.Sort((left, right) =>
            {
                int byScore = right.Score.CompareTo(left.Score);
                return byScore == 0 ? left.PlayerId.CompareTo(right.PlayerId) : byScore;
            });
            foreach (PlayerState player in world.Players)
            {
                int squadOffset = player.Slot * 4;
                ulong mask = 1UL << player.Slot;
                player.Vision.Begin();
                CameraSight? cameraSight = null;
                if (world.CameraVisibility && player.GhostEndTick == 0)
                {
                    var sight = new CameraSight(SquadSystem.FocusPosition(player, joined[squadOffset + player.FocusAnchor - 1]), world.Rules.CameraVisionHalfSize, world.Rules.MapSize);
                    cameraSight = sight;
                    world.Grid.MarkVisible(sight, mask);
                    player.Vision.Reveal(sight);
                }
                foreach (Vector2 source in player.VisionSources)
                {
                    if (cameraSight.HasValue && cameraSight.Value.ContainsCircle(source, world.Rules.VisionRadius)) continue;
                    world.Grid.MarkVisible(source, world.Rules.VisionRadius, mask);
                    player.Vision.Reveal(source, world.Rules.VisionRadius);
                }
                player.Vision.Commit();
                WorldView view = player.View;
                view.ConfigureObservation(world.Rules, player.VisionSources, cameraSight);
                player.PreviousObservedUnits.Clear();
                foreach(UnitView previous in view.UnitBuffer)player.PreviousObservedUnits.Add(previous.Id);
                player.PreviousObservedBuildings.Clear();
                foreach (BuildingView previous in view.BuildingBuffer) player.PreviousObservedBuildings.Add(previous.Id);
                view.Vision = player.Vision;
                view.PlayerId = player.Id; view.Tick = world.Tick; view.LegionPosition = player.LegionPosition;
                view.Direction = player.Direction; view.AppliedSequence = player.AppliedSequence;
                view.FocusAnchor = player.FocusAnchor;
                view.RecallSquad = player.RecallSquad; view.RecallEndTick = player.RecallEndTick;
                view.RecallReadyTick = player.RecallReadyTick; view.RecallArrivalSlots = player.RecallArrivalSlots;
                foreach (SquadState squad in player.Squads)
                {
                    view.SquadBuffer[squad.Id - 1] = new SquadView(squad.Id, squad.Position, squad.Direction, squad.Heading, squad.Destination,
                        squad.Order, squad.AppliedSequence, joined[squadOffset + squad.Id - 1], reinforcing[squadOffset + squad.Id - 1],
                        TrainingSystem.ReservedFor(player, squad.Id), squad.MovementRevision);
                }
                view.BasePosition = player.BasePosition; view.Population = player.Population; view.PopulationCap = player.PopulationCap;
                view.OwnBuildingCount = player.BuildingCount; view.BattleWins = player.BattleWins; view.Standings = world.StandingView;
                view.MainElement = player.MainElement; view.CoreBalance = player.Cores; view.IsTraining = player.TrainingElement >= 0;
                view.Journey = player.Journey;
                view.ReservedPopulation = player.ReservedPopulation; view.BaseLevel = player.BaseLevel;
                view.NatureForge = player.NatureForge; view.EnergyForge = player.EnergyForge;
                view.TrainingElement = player.TrainingElement;
                view.TrainingTargetSquad = player.TrainingQueue.Count == 0 ? (byte)0 : player.TrainingQueue[0].TargetSquad;
                view.TrainingStartTick = player.TrainingStartTick;
                view.TrainingEndTick = player.TrainingEndTick;
                view.TrainingQueueBuffer.Clear();
                for (int index = 1; index < player.TrainingQueue.Count; index++)
                {
                    var order = player.TrainingQueue[index];
                    view.TrainingQueueBuffer.Add(new TrainingOrderView(order.Id, order.Element, order.TargetSquad));
                }
                view.TrainedCount=player.TrainedCount;view.ConvertedCount=player.ConvertedCount;view.GarrisonCount=0;
                view.RallyingCount = 0;
                Array.Clear(view.ElementBuffer,0,view.ElementBuffer.Length);
                view.CandidateBuffer.Clear(); view.UnitBuffer.Clear(); view.CoreBuffer.Clear(); view.EventBuffer.Clear();view.BuildingBuffer.Clear();view.AreaBuffer.Clear();
                foreach (UnitState unit in world.Units)
                    if (unit.Health > 0 && (unit.OwnerId == player.Id || (unit.VisibleTo & mask) != 0))
                    {
                        view.UnitBuffer.Add(new UnitView(unit, world.Rules.Elements[unit.Element].Hp,world.Time, unit.OwnerId == player.Id));
                        if (unit.OwnerId == player.Id)
                        {
                            view.ElementBuffer[unit.Element]++;
                            if (unit.Garrison || unit.IsReinforcing)
                            {
                                view.GarrisonCount++;
                                if (player.Rallies(unit)) view.RallyingCount++;
                            }
                        }
                    }
                view.GarrisonArrivals = player.GarrisonArrivals;
                foreach(BuildingState building in world.Buildings)
                    if(building.OwnerId==player.Id||view.CanSeePoint(building.Position))
                        view.BuildingBuffer.Add(new BuildingView(building, world.PlayerById[building.OwnerId].GhostEndTick));
                view.OwnerColorBuffer.Clear();
                ulong coloredOwners = 0;
                foreach (PlayerStanding standing in world.Standings) coloredOwners |= 1UL << world.PlayerById[standing.PlayerId].Slot;
                foreach (UnitView unit in view.UnitBuffer)
                    if (unit.OwnerId >= 0) coloredOwners |= 1UL << world.PlayerById[unit.OwnerId].Slot;
                foreach (BuildingView building in view.BuildingBuffer) coloredOwners |= 1UL << world.PlayerById[building.OwnerId].Slot;
                foreach (PlayerState owner in world.Players)
                    if ((coloredOwners & (1UL << owner.Slot)) != 0) view.OwnerColorBuffer.Add(new OwnerColorView(owner.Id, owner.ColorId));
                foreach(AreaState area in world.Areas)
                    if(area.Expires>world.Time&&view.CanSeePoint(area.Position))view.AreaBuffer.Add(new AreaView(area,world.Rules.TickRate));
                foreach (ConversionCandidateState candidate in world.Candidates)
                    if (view.CanSeePoint(candidate.Position))
                    {
                        RecoveryRole role = candidate.SecureOwner == player.Id ? RecoveryRole.Secure : candidate.PreviousOwner == player.Id && !candidate.RescuedOnce ? RecoveryRole.Rescue : RecoveryRole.Observer;
                        view.CandidateBuffer.Add(new ConversionCandidateView(candidate.Id, candidate.Element, candidate.Position, candidate.EndTick, role,
                            role == RecoveryRole.Secure ? candidate.SecureProgress : role == RecoveryRole.Rescue ? candidate.RescueProgress : 0));
                    }
                foreach (CoreState core in world.Cores)
                    if (view.CanSeePoint(core.Position)) view.CoreBuffer.Add(new CoreView(core,world.Rules.TickRate));
                foreach (SimEvent item in world.Events)
                {
                    if (item.Kind == SimEventKind.BaseUnderAttack)
                    {
                        if (item.OwnerId == player.Id) view.EventBuffer.Add(item);
                        continue;
                    }
                    bool command = item.Kind == SimEventKind.CommandRejected || item.Kind == SimEventKind.CommandApplied;
                    if (command && item.OwnerId != player.Id) continue;
                    bool personal = item.OwnerId == player.Id && (command || item.Kind == SimEventKind.CorePickedUp || item.Kind == SimEventKind.Trained);
                    if (!personal && !view.CanSeePoint(item.Position)) continue;
                    bool sourceKnown=Known(world,player,item.SourceId,item.SourceKind,mask)||item.HasSourcePosition&&WasObserved(player,item.SourceId,item.SourceKind)&&view.CanSeePoint(item.SourcePosition);
                    bool targetKnown=Known(world,player,item.TargetId,item.TargetKind,mask)||WasObserved(player,item.TargetId,item.TargetKind)&&view.CanSeePoint(item.Position);
                    bool victimElement=item.Kind==SimEventKind.Death||item.Kind==SimEventKind.Converted||item.Kind==SimEventKind.Rescued||item.Kind==SimEventKind.Trained;
                    bool ownerKnown=sourceKnown||item.OwnerId==player.Id||targetKnown&&(item.Kind==SimEventKind.Converted||item.Kind==SimEventKind.Rescued||item.Kind==SimEventKind.Trained);
                    bool originKnown=sourceKnown&&item.HasSourcePosition&&view.CanSeePoint(item.SourcePosition);
                    int entityId=(item.EntityKind==ObservedEntityKind.Unit||item.EntityKind==ObservedEntityKind.Building)&&!targetKnown?0:item.EntityId;
                    view.EventBuffer.Add(new SimEvent(item.Kind,ownerKnown?item.OwnerId:-1,
                        sourceKnown?item.SourceId:0,targetKnown?item.TargetId:0,item.Position,item.Amount,item.Detail,item.Sequence,
                        (victimElement?targetKnown:sourceKnown)?item.Element:-1,originKnown?item.SourcePosition:default,originKnown,entityId,item.EntityKind,item.SourceKind,item.TargetKind));
                }
            }
            world.Metrics.PlayerUnits = 0; world.Metrics.NeutralUnits = 0;
            world.Metrics.ReservedUnitSlots = world.ReservedUnitSlots;
            world.Metrics.Candidates = world.Candidates.Count;
            world.Metrics.Ghosts = world.GhostCount;
            world.Metrics.EcologyNeutralUnits = 0;
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0)
                {
                    if (unit.OwnerId >= 0) world.Metrics.PlayerUnits++;
                    else { world.Metrics.NeutralUnits++; if (unit.EcologyNeutral) world.Metrics.EcologyNeutralUnits++; }
                }
            world.Metrics.Tick = world.Tick; world.Metrics.DroppedCores = world.Cores.Count;
        }
        private static bool Known(WorldState world, PlayerState player, int id, ObservedEntityKind kind, ulong mask)
        {
            if (id == 0) return false;
            if (kind == ObservedEntityKind.Building)
                return world.BuildingById.TryGetValue(id, out BuildingState building) &&
                    (building.OwnerId == player.Id || player.View.CanSeePoint(building.Position));
            return world.UnitById.TryGetValue(id, out UnitState unit) && (unit.OwnerId == player.Id || (unit.VisibleTo & mask) != 0);
        }
        private static bool WasObserved(PlayerState player, int id, ObservedEntityKind kind)
            => (kind == ObservedEntityKind.Building ? player.PreviousObservedBuildings : player.PreviousObservedUnits).Contains(id);
    }

    /// <summary>서버 군단 중심에서 계산한 카메라의 지면 시야다.</summary>
    internal readonly struct CameraSight
    {
        internal readonly Vector2 Min, Max;

        internal CameraSight(Vector2 center, Vector2 halfSize, float mapSize)
        {
            Min = Vector2.Max(Vector2.Zero, center - halfSize);
            Max = Vector2.Min(new Vector2(mapSize), center + halfSize);
        }

        internal bool Contains(Vector2 point) =>
            point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;

        internal bool ContainsCircle(Vector2 center, float radius)
        {
            float left = center.X - Min.X, right = Max.X - center.X;
            float bottom = center.Y - Min.Y, top = Max.Y - center.Y;
            float squared = radius * radius;
            // 기존 원 판정과 같은 제곱 거리로 여유를 확인하고 접선·반올림 동률은 원래 순회를 유지한다.
            return left > 0 && right > 0 && bottom > 0 && top > 0 &&
                left * left > squared && right * right > squared && bottom * bottom > squared && top * top > squared;
        }
    }
}
