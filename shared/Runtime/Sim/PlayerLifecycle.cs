using System;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>본진 파괴와 유령 만료를 전투 이후 한 번씩 처리한다.</summary>
    internal static class PlayerLifecycle
    {
        internal static void Resolve(WorldState world, EconomySystem economy)
        {
            bool ghostDefeated = false;
            foreach (BuildingState building in world.Buildings)
            {
                if (building.Health > 0 || building.Kind != BuildingKind.Base ||
                    world.PlayerById[building.OwnerId].GhostEndTick == 0) continue;
                foreach (UnitState unit in world.Units)
                {
                    if (unit.OwnerId != building.OwnerId) continue;
                    unit.Health = 0;
                    unit.Dead = true;
                    unit.KillerOwner = building.KillerOwner;
                    unit.KillerSource = building.KillerSource;
                    unit.KillerElement = building.KillerElement;
                    unit.KillerTower = false;
                    ghostDefeated = true;
                }
            }
            if (ghostDefeated) economy.ResolveDeaths(world);

            for (int index = world.Players.Count - 1; index >= 0; index--)
            {
                PlayerState player = world.Players[index];
                bool ghost = player.GhostEndTick != 0;
                if (!player.BaseDestroyed && (!ghost || player.GhostEndTick > world.Tick)) continue;
                RemovePlayer(world, player, index, ghost);
            }

            for (int index = world.Buildings.Count - 1; index >= 0; index--)
            {
                BuildingState building = world.Buildings[index];
                if (building.Health > 0) continue;
                PlayerState owner = world.PlayerById[building.OwnerId];
                if (!building.IsUnderConstruction)
                {
                    owner.BuildingCount--;
                    if (building.Kind == BuildingKind.Dome) owner.Dome = null;
                    if (building.Kind == BuildingKind.NatureForge || building.Kind == BuildingKind.EnergyForge)
                    {
                        bool nature = building.Kind == BuildingKind.NatureForge;
                        if (nature) owner.NatureForge = false;
                        else owner.EnergyForge = false;
                        TrainingSystem.LoseForge(world, owner, nature ? Data.ElementGroup.Nature : Data.ElementGroup.Energy);
                    }
                }
                world.BuildingById.Remove(building.Id);
                world.Buildings.RemoveAt(index);
            }
        }

        private static void RemovePlayer(WorldState world, PlayerState player, int playerIndex, bool ghost)
        {
            for (int index = world.Units.Count - 1; index >= 0; index--)
            {
                UnitState unit = world.Units[index];
                if (unit.OwnerId != player.Id) continue;
                if (ghost)
                    Drop(world, unit.Position, (int)decimal.Floor(world.Rules.BaseCost(unit.Element) * world.Rules.GhostUnitCoreRatio));
                unit.Health = 0;
                world.UnitById.Remove(unit.Id);
                world.Units.RemoveAt(index);
            }
            for (int index = world.Buildings.Count - 1; index >= 0; index--)
            {
                BuildingState building = world.Buildings[index];
                if (building.OwnerId != player.Id) continue;
                if (ghost && building.Health > 0) Drop(world, building.Position, world.Rules.GhostBuildingCoreFlat);
                building.Health = 0;
                world.BuildingById.Remove(building.Id);
                world.Buildings.RemoveAt(index);
            }
            world.Areas.RemoveAll(area => area.Owner == player.Id);
            world.PlayerById.Remove(player.Id);
            world.Players.RemoveAt(playerIndex);
            if (ghost) world.GhostCount--;
        }

        private static void Drop(WorldState world, System.Numerics.Vector2 position, int amount)
        {
            if (amount == 0) return;
            int id = EconomySystem.DropCore(world, position, amount);
            world.Events.Add(new SimEvent(SimEventKind.CoreDropped, -1, 0, 0, position, amount,
                entityId: id, entityKind: ObservedEntityKind.Core));
            world.Metrics.Drops++;
        }
    }
}
