using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>양성 접수 때 자원과 인구 슬롯을 예약하고 완료·합류를 틱 경계에서 확정한다.</summary>
    internal static class TrainingSystem
    {
        internal static CommandFailure Start(WorldState world, PlayerState player, int element)
        {
            if (player.TrainingElement >= 0) return CommandFailure.Busy;
            if (player.Population >= player.PopulationCap) return CommandFailure.PopulationCap;
            bool nature = world.Rules.Elements[element].Group == ElementGroup.Nature;
            if (!(nature ? player.NatureForge : player.EnergyForge) || !world.Rules.IsUnlocked(player.BaseLevel, nature ? "forge_nature" : "forge_energy")) return CommandFailure.NotUnlocked;
            int cost = world.Rules.TrainingCost(element, player.MainElement);
            if (player.Cores < cost) return CommandFailure.InsufficientCores;
            player.Cores -= cost; player.TrainingElement = element;
            player.TrainingEnd = world.Time + (nature ? world.Rules.NatureTrainSeconds : world.Rules.EnergyTrainSeconds);
            return CommandFailure.None;
        }

        internal static void Tick(WorldState world)
        {
            foreach (PlayerState player in world.Players)
            {
                if (player.TrainingElement < 0 || world.Time < player.TrainingEnd) continue;
                int element = player.TrainingElement;
                player.TrainingElement = -1;
                Vector2 position = Vector2.Clamp(player.BasePosition + new Vector2(0, world.Rules.BaseRadius + world.Rules.SlotSpacing), Vector2.Zero, new Vector2(world.Rules.MapSize));
                UnitState unit = world.AddUnit(player.Id, element, position, garrison: true);
                world.Metrics.Trained++;
                world.Events.Add(new SimEvent(SimEventKind.Trained, player.Id, 0, unit.Id, position));
            }
            foreach (UnitState unit in world.Units)
            {
                if (!unit.Garrison || unit.OwnerId < 0) continue;
                PlayerState player = world.PlayerById[unit.OwnerId];
                float baseDistance = Vector2.DistanceSquared(player.LegionPosition, player.BasePosition);
                float unitDistance = Vector2.DistanceSquared(player.LegionPosition, unit.Position);
                float joinSquared = world.Rules.GarrisonJoinRadius * world.Rules.GarrisonJoinRadius;
                if (baseDistance <= joinSquared && Vector2.DistanceSquared(unit.Position, player.BasePosition) <= joinSquared || player.Rallying && unitDistance <= world.Rules.SlotSpacing * world.Rules.SlotSpacing)
                { unit.Garrison = false; player.FormationDirty = true; }
            }
        }
    }
}
