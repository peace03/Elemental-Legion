using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>유닛·건물의 원형 시야를 합치고 플레이어에게 허용된 값만 재사용 버퍼에 담는다.</summary>
    internal static class VisibilitySystem
    {
        internal static void Refresh(WorldState world)
        {
            world.Grid.Rebuild(world.Units);
            foreach (UnitState unit in world.Units) unit.VisibleTo = 0;
            foreach (PlayerState player in world.Players) player.VisionSources.Clear();
            foreach (UnitState unit in world.Units)
                if (unit.OwnerId >= 0 && unit.Health > 0) world.PlayerById[unit.OwnerId].VisionSources.Add(unit.Position);
            foreach (BuildingState building in world.Buildings) world.PlayerById[building.OwnerId].VisionSources.Add(building.Position);
            foreach (PlayerState player in world.Players)
            {
                ulong mask = 1UL << player.Slot;
                foreach (Vector2 source in player.VisionSources) world.Grid.MarkVisible(source, world.Rules.VisionRadius, mask);
                WorldView view = player.View;
                view.PlayerId = player.Id; view.Tick = world.Tick; view.LegionPosition = player.LegionPosition;
                view.BasePosition = player.BasePosition; view.Population = player.Population; view.PopulationCap = player.PopulationCap;
                view.MainElement = player.MainElement; view.CoreBalance = player.Cores; view.IsTraining = player.TrainingElement >= 0;
                view.ReservedPopulation = player.TrainingElement >= 0 ? 1 : 0; view.BaseLevel = player.BaseLevel;
                view.NatureForge = player.NatureForge; view.EnergyForge = player.EnergyForge;
                view.UnitBuffer.Clear(); view.CoreBuffer.Clear(); view.EventBuffer.Clear();
                foreach (UnitState unit in world.Units)
                    if (unit.Health > 0 && (unit.OwnerId == player.Id || (unit.VisibleTo & mask) != 0)) view.UnitBuffer.Add(new UnitView(unit, world.Rules.Elements[unit.Element].Hp));
                foreach (CoreState core in world.Cores)
                    if (CanSeePoint(world, player, core.Position)) view.CoreBuffer.Add(new CoreView(core));
                foreach (SimEvent item in world.Events)
                {
                    if (item.Kind == SimEventKind.CommandRejected && item.OwnerId != player.Id) continue;
                    bool personal = item.OwnerId == player.Id && (item.Kind == SimEventKind.CommandRejected || item.Kind == SimEventKind.CorePickedUp || item.Kind == SimEventKind.Trained);
                    if (!personal && !CanSeePoint(world, player, item.Position)) continue;
                    bool sourceKnown = Known(world, player, item.SourceId, mask);
                    view.EventBuffer.Add(new SimEvent(item.Kind, sourceKnown || item.OwnerId == player.Id ? item.OwnerId : -1,
                        sourceKnown ? item.SourceId : 0, Known(world, player, item.TargetId, mask) ? item.TargetId : 0,
                        item.Position, item.Amount, item.Detail, item.Sequence));
                }
            }
            world.Metrics.PlayerUnits = 0; world.Metrics.NeutralUnits = 0;
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0) { if (unit.OwnerId >= 0) world.Metrics.PlayerUnits++; else world.Metrics.NeutralUnits++; }
            world.Metrics.Tick = world.Tick; world.Metrics.DroppedCores = world.Cores.Count;
        }
        private static bool Known(WorldState world, PlayerState player, int id, ulong mask) => id != 0 && world.UnitById.TryGetValue(id, out UnitState unit) && (unit.OwnerId == player.Id || (unit.VisibleTo & mask) != 0);
        private static bool CanSeePoint(WorldState world, PlayerState player, Vector2 point)
        {
            float radiusSquared = world.Rules.VisionRadius * world.Rules.VisionRadius;
            foreach (Vector2 source in player.VisionSources)
                if (Vector2.DistanceSquared(source, point) <= radiusSquared) return true;
            return false;
        }
    }
}
