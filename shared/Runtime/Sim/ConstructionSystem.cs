using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>본진의 건설과 업그레이드를 서버 틱의 명령 순서대로 확정한다.</summary>
    internal static class ConstructionSystem
    {
        internal static bool ValidIntent(GameRules rules, SimCommand command)
        {
            BuildingKind kind = command.BuildingKind;
            if (kind <= BuildingKind.Base || kind > BuildingKind.Dome || command.CellX < 0 || command.CellZ < 0) return false;
            if (command.CellX > rules.MapSize / rules.BuildGridSize || command.CellZ > rules.MapSize / rules.BuildGridSize) return false;
            if (kind == BuildingKind.Dome && (command.CellX != 0 || command.CellZ != 0)) return false;
            return ValidTowerElement(rules, kind, command.TowerElement);
        }

        internal static bool ValidTowerElement(GameRules rules, BuildingKind kind, int element)
            => IsTower(kind)
                ? element >= 0 && element < rules.Elements.Count &&
                    rules.Elements[element].Group == (kind == BuildingKind.NatureTower ? ElementGroup.Nature : ElementGroup.Energy)
                : element == -1;

        internal static CommandFailure Build(WorldState world, PlayerState player, SimCommand command)
        {
            GameRules rules = world.Rules;
            BuildingKind kind = command.BuildingKind;
            if (!rules.IsBuildingUnlocked(player.BaseLevel, kind)) return CommandFailure.NotUnlocked;
            int cost = rules.BuildingCost(kind);
            if (player.Cores < cost) return CommandFailure.InsufficientCores;

            bool tower = IsTower(kind);
            int count = 0;
            foreach (BuildingState building in world.Buildings)
                if (building.OwnerId == player.Id && (tower ? IsTower(building.Kind) : building.Kind == kind)) count++;
            if (count >= (tower ? rules.TowerSlots(player.BaseLevel) : 1)) return CommandFailure.BuildingLimit;

            float radius = rules.BuildingRadius(kind);
            Vector2 position = kind == BuildingKind.Dome ? player.BasePosition : new Vector2(command.CellX, command.CellZ) * rules.BuildGridSize;
            float reach = rules.BuildRadius - radius;
            if (reach < 0 || position.X < radius || position.Y < radius || position.X > rules.MapSize - radius ||
                position.Y > rules.MapSize - radius || Vector2.DistanceSquared(position, player.BasePosition) > reach * reach)
                return CommandFailure.InvalidPlacement;
            if (kind != BuildingKind.Dome && Occupied(world, position, radius)) return CommandFailure.InvalidPlacement;

            player.Cores -= cost;
            long duration = rules.BuildingConstructionTicks(kind);
            world.AddBuilding(player, kind, position, command.TowerElement, duration == 0 ? 0 : world.Tick + duration);
            return CommandFailure.None;
        }

        internal static void CompleteDueBuildings(WorldState world)
        {
            foreach (BuildingState building in world.Buildings)
                if (building.IsUnderConstruction && building.ConstructionEndTick <= world.Tick)
                    WorldState.ActivateBuilding(world.PlayerById[building.OwnerId], building);
        }

        internal static void CancelPendingBuildings(WorldState world, PlayerState player)
        {
            for (int index = world.Buildings.Count - 1; index >= 0; index--)
            {
                BuildingState building = world.Buildings[index];
                if (building.OwnerId != player.Id || !building.IsUnderConstruction) continue;
                player.Cores += world.Rules.BuildingCost(building.Kind);
                world.BuildingById.Remove(building.Id);
                world.Buildings.RemoveAt(index);
            }
        }

        internal static CommandFailure Upgrade(WorldState world, PlayerState player)
        {
            int level = player.BaseLevel + 1;
            if (level > world.Rules.MaxBaseLevel) return CommandFailure.MaxLevel;
            int cost = world.Rules.UpgradeCost(level);
            if (player.Cores < cost) return CommandFailure.InsufficientCores;
            player.Cores -= cost;
            player.BaseLevel = level;
            player.PopulationCap = world.Rules.PopulationCap(level);
            return CommandFailure.None;
        }

        internal static bool IsTower(BuildingKind kind) => kind == BuildingKind.NatureTower || kind == BuildingKind.EnergyTower;

        private static bool Occupied(WorldState world, Vector2 position, float radius)
        {
            foreach (BuildingState building in world.Buildings)
            {
                float clearance = building.Radius + radius;
                if (building.BlocksMovement && Vector2.DistanceSquared(building.Position, position) < clearance * clearance) return true;
            }
            float squared = radius * radius;
            foreach (PlayerState player in world.Players)
                if (Vector2.DistanceSquared(player.LegionPosition, position) < squared) return true;
            foreach (UnitState unit in world.Units)
                if (unit.Health > 0 && Vector2.DistanceSquared(unit.Position, position) < squared) return true;
            return false;
        }
    }
}
