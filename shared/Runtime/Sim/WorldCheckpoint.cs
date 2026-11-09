using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using Newtonsoft.Json;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>서버 저장용 틱 복사본이다. 명령 큐, 현재 시야, 전송 상태와 런타임 참조는 저장하지 않는다.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class WorldCheckpoint
    {
        [JsonProperty(Required = Required.Always)] public int SchemaVersion { get; private set; }
        [JsonProperty(Required = Required.Always)] public string DataHash { get; private set; }
        [JsonProperty(Required = Required.Always)] public long Tick { get; private set; }
        [JsonProperty] public int RoomPlayers { get; private set; }
        public int MaxPlayerId => Players.Length == 0 ? -1 : Players[Players.Length - 1].Id;
        [JsonProperty(Required = Required.Always)] private int NextId, NextBuildingId, NextAreaId;
        [JsonProperty(Required = Required.Always)] private int Seed;
        [JsonProperty(Required = Required.Always)] private PlayerData[] Players;
        [JsonProperty(Required = Required.Always)] private UnitData[] Units;
        [JsonProperty(Required = Required.Always)] private BuildingData[] Buildings;
        [JsonProperty(Required = Required.Always)] private CoreData[] Cores;
        [JsonProperty(Required = Required.Always)] private AreaData[] Areas;
        [JsonProperty(Required = Required.AllowNull)] internal EcologyData Ecology;

        [JsonConstructor]
        private WorldCheckpoint() { }

        internal static WorldCheckpoint Capture(WorldState world, WorldEcology ecology)
        {
            var snapshot = new WorldCheckpoint
            {
                SchemaVersion = 2, DataHash = world.Rules.DataHash, Tick = world.Tick, RoomPlayers = world.Rules.RoomPlayers, Seed = world.Seed,
                NextId = world.NextId, NextBuildingId = world.NextBuildingId, NextAreaId = world.NextAreaId,
                Players = new PlayerData[world.Players.Count], Units = new UnitData[world.Units.Count],
                Buildings = new BuildingData[world.Buildings.Count], Cores = new CoreData[world.Cores.Count],
                Areas = new AreaData[world.Areas.Count], Ecology = ecology?.Capture()
            };
            for (int index = 0; index < snapshot.Players.Length; index++)
            {
                PlayerState player = world.Players[index];
                var orders = new TrainingData[player.TrainingQueue.Count];
                for (int order = 0; order < orders.Length; order++)
                {
                    var item = player.TrainingQueue[order];
                    orders[order] = new TrainingData { Id = item.Id, Element = item.Element, Cost = item.Cost };
                }
                snapshot.Players[index] = new PlayerData
                {
                    Id = player.Id, Nickname = player.Nickname, ColorId = player.ColorId, Slot = player.Slot, MainElement = player.MainElement,
                    BaseLevel = player.BaseLevel, BasePosition = player.BasePosition, LegionPosition = player.LegionPosition,
                    Heading = player.Heading, Cores = player.Cores, GhostEndTick = player.GhostEndTick,
                    BattleWins = player.BattleWins, Trained = player.TrainedCount, Converted = player.ConvertedCount,
                    GarrisonArrivals = player.GarrisonArrivals, Explored = player.Vision.Explored.ToArray(), Training = orders,
                    TrainingStartTick = player.TrainingStartTick, TrainingEndTick = player.TrainingEndTick,
                    TrainingPausedAtTick = player.TrainingPausedAtTick, NextTrainingOrderId = player.NextTrainingOrderId
                };
            }
            for (int index = 0; index < snapshot.Units.Length; index++)
            {
                UnitState unit = world.Units[index];
                var statuses = new StatusData[unit.Statuses.Length];
                for (int status = 0; status < statuses.Length; status++)
                {
                    TimedStatus value = unit.Statuses[status];
                    statuses[status] = new StatusData
                    {
                        Expires = value.Expires, Amount = value.Amount, Dps = value.Dps, Owner = value.Owner,
                        Source = value.Source, Element = value.Element, LegionOwner = value.LegionOwner
                    };
                }
                snapshot.Units[index] = new UnitData
                {
                    Id = unit.Id, Owner = unit.OwnerId, Element = unit.Element, Position = unit.Position, Home = unit.Home,
                    Health = unit.Health, Shield = unit.Shield, AttackRemaining = unit.AttackRemaining,
                    ShieldRemaining = unit.ShieldRemaining, RangeMultiplier = unit.RangeMultiplier, SpeedMultiplier = unit.SpeedMultiplier,
                    Haste = unit.Haste, SmokeDebuff = unit.SmokeDebuff, SteamDebuff = unit.SteamDebuff, SandDebuff = unit.SandDebuff,
                    Converted = unit.Converted, Garrison = unit.Garrison, WasNeutral = unit.WasNeutral,
                    EcologyNeutral = unit.EcologyNeutral, NeutralReturning = unit.NeutralReturning, CatchingUp = unit.CatchingUp,
                    Target = unit.Target?.Id ?? 0, BuildingTarget = unit.BuildingTarget?.Id ?? 0,
                    Detour = unit.Detour?.Id ?? 0, DetourSide = unit.DetourSide,
                    Statuses = statuses, LastHitTimes = (double[])unit.LastHitTimes.Clone(),
                    LastHitOwners = (int[])unit.LastHitOwners.Clone(),
                    KillerOwner = unit.KillerOwner, KillerSource = unit.KillerSource, KillerElement = unit.KillerElement,
                    KillerTower = unit.KillerTower
                };
            }
            for (int index = 0; index < snapshot.Buildings.Length; index++)
            {
                BuildingState building = world.Buildings[index];
                snapshot.Buildings[index] = new BuildingData
                {
                    Id = building.Id, Owner = building.OwnerId, Kind = building.Kind, Position = building.Position,
                    TowerElement = building.TowerElement, Health = building.Health, AttackRemaining = building.AttackRemaining,
                    ConstructionEndTick = building.ConstructionEndTick, KillerOwner = building.KillerOwner,
                    KillerSource = building.KillerSource, KillerElement = building.KillerElement
                };
            }
            for (int index = 0; index < snapshot.Cores.Length; index++)
            {
                CoreState core = world.Cores[index];
                snapshot.Cores[index] = new CoreData { Id = core.Id, Amount = core.Amount, Position = core.Position, Expires = core.Expires };
            }
            for (int index = 0; index < snapshot.Areas.Length; index++)
            {
                AreaState area = world.Areas[index];
                snapshot.Areas[index] = new AreaData
                {
                    Id = area.Id, Reaction = world.Rules.Reactions.IndexOf(area.Definition), Owner = area.Owner,
                    Source = area.Source, Element = area.Element, Target = area.Target, Position = area.Position, Expires = area.Expires
                };
            }
            return snapshot;
        }

        internal WorldState Restore(GameRules rules, bool collectTimings)
        {
            Check(SchemaVersion == 2, "월드 스키마 버전");
            Check(string.Equals(rules.DataHash, DataHash, StringComparison.Ordinal), "게임 데이터 해시");
            Check(Tick >= 0 && Tick < long.MaxValue / 2 && NextId > 0 && NextBuildingId > 0 && NextAreaId > 0, "틱 또는 다음 개체 번호");
            Check(Players != null && Players.Length <= rules.MaxPlayers &&
                Units != null && Units.Length <= Protocol.MaxUnits &&
                Buildings != null && Buildings.Length <= GameplayCodec.MaxBuildings &&
                Cores != null && Cores.Length <= GameplayCodec.MaxObjects &&
                Areas != null && Areas.Length <= GameplayCodec.MaxObjects, "개체 수");
            var world = new WorldState(rules, Seed, collectTimings, Tick)
            {
                NextId = NextId, NextBuildingId = NextBuildingId, NextAreaId = NextAreaId,
                ProtectionEnabled = Ecology != null
            };
            RestorePlayers(world);
            RestoreBuildings(world);
            RestoreUnits(world);

            int previous = 0;
            foreach (CoreData saved in Cores)
            {
                Check(saved.Id > previous && saved.Id < NextId && !world.UnitById.ContainsKey(saved.Id) && saved.Amount > 0 &&
                    Inside(rules, saved.Position) && Finite(saved.Expires) && saved.Expires > world.Time, "핵");
                world.Cores.Add(new CoreState { Id = saved.Id, Amount = saved.Amount, Position = saved.Position, Expires = saved.Expires });
                previous = saved.Id;
            }
            previous = 0;
            foreach (AreaData saved in Areas)
            {
                Check(saved.Id > previous && saved.Id < NextAreaId && saved.Reaction >= 0 && saved.Reaction < rules.Reactions.Count &&
                    world.PlayerById.ContainsKey(saved.Owner) && saved.Source > 0 && saved.Source < NextId &&
                    saved.Target > 0 && saved.Target < NextId && Element(rules, saved.Element) && Inside(rules, saved.Position) &&
                    Finite(saved.Expires) && saved.Expires > world.Time - rules.DeltaTime, "반응 영역");
                ReactionDefinition reaction = rules.Reactions[saved.Reaction];
                Check(reaction.Kind == ReactionKind.Steam || reaction.Kind == ReactionKind.Sandstorm, "영역 반응 종류");
                world.Areas.Add(new AreaState
                {
                    Id = saved.Id, Definition = reaction, Owner = saved.Owner, Source = saved.Source,
                    Element = saved.Element, Target = saved.Target, Position = saved.Position, Expires = saved.Expires
                });
                previous = saved.Id;
            }
            return world;
        }

        private void RestorePlayers(WorldState world)
        {
            GameRules rules = world.Rules;
            int previous = -1;
            ulong slots = 0;
            int usedColors = 0;
            foreach (PlayerData saved in Players)
                if (saved?.ColorId != null)
                {
                    Check(PlayerPalette.IsValid(saved.ColorId.Value) && (usedColors & (1 << saved.ColorId.Value)) == 0, "소유 색");
                    usedColors |= 1 << saved.ColorId.Value;
                }
            foreach (PlayerData saved in Players)
            {
                Check(saved != null && saved.Id > previous && saved.Slot >= 0 && saved.Slot < rules.MaxPlayers &&
                    (slots & (1UL << saved.Slot)) == 0 && PlayerNickname.IsValid(saved.Nickname) &&
                    rules.StartingElements.Contains(saved.MainElement) && saved.BaseLevel >= 1 && saved.BaseLevel <= rules.MaxBaseLevel &&
                    Inside(rules, saved.BasePosition) && Inside(rules, saved.LegionPosition) && saved.Heading.LengthSquared() <= 1.001f &&
                    saved.Cores >= 0 && saved.BattleWins >= 0 && saved.Trained >= 0 && saved.Converted >= 0 && saved.GarrisonArrivals >= 0 &&
                    (saved.GhostEndTick == 0 || saved.GhostEndTick >= Tick &&
                        (saved.GhostEndTick - Tick <= rules.GhostDecayTicks || Tick == 0 && saved.GhostEndTick == 1)), "플레이어");
                int colorId = saved.ColorId ?? 0;
                if (!saved.ColorId.HasValue)
                {
                    while (colorId < PlayerPalette.Count && (usedColors & (1 << colorId)) != 0) colorId++;
                    Check(PlayerPalette.IsValid(colorId), "소유 색 부족");
                    usedColors |= 1 << colorId;
                }
                var player = new PlayerState
                {
                    ColorId = colorId, Id = saved.Id, Nickname = saved.Nickname, Slot = saved.Slot, MainElement = saved.MainElement, BaseLevel = saved.BaseLevel,
                    BasePosition = saved.BasePosition, LegionPosition = saved.LegionPosition, Heading = saved.Heading,
                    Cores = saved.Cores, GhostEndTick = saved.GhostEndTick, BattleWins = saved.BattleWins,
                    TrainedCount = saved.Trained, ConvertedCount = saved.Converted, GarrisonArrivals = saved.GarrisonArrivals,
                    PopulationCap = rules.PopulationCap(saved.BaseLevel), Vision = new VisionGrid(rules.MapSize, rules.CellSize)
                };
                Check(player.Vision.TryRestoreExplored(saved.Explored), "탐색 비트맵");
                Check(saved.Training != null && saved.Training.Length <= player.PopulationCap && saved.NextTrainingOrderId > 0,
                    "양성 대기열");
                ulong order = 0;
                foreach (TrainingData item in saved.Training)
                {
                    Check(item.Id > order && item.Id < saved.NextTrainingOrderId && Element(rules, item.Element) &&
                        item.Cost == rules.TrainingCost(item.Element, saved.MainElement), "양성 예약");
                    player.TrainingQueue.Add((item.Id, item.Element, item.Cost));
                    order = item.Id;
                }
                player.TrainingStartTick = saved.TrainingStartTick;
                player.TrainingEndTick = saved.TrainingEndTick;
                player.TrainingPausedAtTick = saved.TrainingPausedAtTick;
                player.NextTrainingOrderId = saved.NextTrainingOrderId;
                ValidateTrainingTimer(world, player);
                world.Players.Add(player);
                world.PlayerById.Add(saved.Id, player);
                if (saved.GhostEndTick != 0) world.GhostCount++;
                slots |= 1UL << saved.Slot;
                previous = saved.Id;
            }
        }

        private void RestoreBuildings(WorldState world)
        {
            GameRules rules = world.Rules;
            var bases = new HashSet<int>();
            int previous = 0;
            foreach (BuildingData saved in Buildings)
            {
                Check(saved.Id > previous && saved.Id < NextBuildingId && saved.Kind <= BuildingKind.Dome &&
                    world.PlayerById.ContainsKey(saved.Owner) && Inside(rules, saved.Position) &&
                    ConstructionSystem.ValidTowerElement(rules, saved.Kind, saved.TowerElement) &&
                    saved.Health > 0 && saved.Health <= rules.BuildingHealth(saved.Kind) && Finite(saved.AttackRemaining) &&
                    saved.KillerOwner >= -1 && saved.KillerSource >= 0 && saved.KillerSource < NextId && Element(rules, saved.KillerElement),
                    "건물");
                PlayerState player = world.PlayerById[saved.Owner];
                Check(saved.ConstructionEndTick == 0 || (saved.ConstructionEndTick > Tick &&
                    saved.ConstructionEndTick - Tick <= rules.BuildingConstructionTicks(saved.Kind) && player.GhostEndTick == 0),
                    "건설 타이머");
                if (saved.Kind == BuildingKind.Base)
                    Check(saved.ConstructionEndTick == 0 && saved.Position == player.BasePosition && bases.Add(player.Id), "본진");
                var building = new BuildingState(saved.Id, saved.Owner, saved.Kind, saved.Position, rules, saved.TowerElement)
                {
                    Health = saved.Health, AttackRemaining = saved.AttackRemaining, ConstructionEndTick = saved.ConstructionEndTick,
                    KillerOwner = saved.KillerOwner, KillerSource = saved.KillerSource, KillerElement = saved.KillerElement
                };
                world.Buildings.Add(building);
                world.BuildingById.Add(building.Id, building);
                if (!building.IsUnderConstruction) WorldState.ActivateBuilding(player, building);
                previous = saved.Id;
            }
            Check(bases.Count == Players.Length, "본진 수");
        }

        private void RestoreUnits(WorldState world)
        {
            GameRules rules = world.Rules;
            int previous = 0;
            foreach (UnitData saved in Units)
            {
                Check(saved != null && saved.Id > previous && saved.Id < NextId && Element(rules, saved.Element) &&
                    (saved.Owner == -1 && !saved.Garrison || world.PlayerById.ContainsKey(saved.Owner)) &&
                    Inside(rules, saved.Position) && Finite(saved.Home.X) && Finite(saved.Home.Y) &&
                    saved.Health > 0 && saved.Health <= rules.Elements[saved.Element].Hp &&
                    Finite(saved.Shield) && saved.Shield >= 0 && Finite(saved.RangeMultiplier) && saved.RangeMultiplier >= 0 &&
                    Finite(saved.SpeedMultiplier) && saved.SpeedMultiplier >= 0 && Finite(saved.Haste) && saved.Haste >= 0 &&
                    Debuff(saved.SmokeDebuff) && Debuff(saved.SteamDebuff) && Debuff(saved.SandDebuff) &&
                    Finite(saved.AttackRemaining) && Finite(saved.ShieldRemaining) &&
                    saved.Target >= 0 && saved.Target < NextId && saved.BuildingTarget >= 0 && saved.BuildingTarget < NextBuildingId &&
                    saved.Detour >= 0 && saved.Detour < NextBuildingId && saved.DetourSide >= -1 && saved.DetourSide <= 1 &&
                    saved.KillerOwner >= -1 && saved.KillerSource >= 0 && saved.KillerSource < (saved.KillerTower ? NextBuildingId : NextId) &&
                    Element(rules, saved.KillerElement), "유닛");
                var unit = new UnitState(saved.Id, saved.Owner, saved.Element, saved.Position, saved.Health, saved.Converted,
                    saved.Garrison, saved.WasNeutral, rules.Elements.Count)
                {
                    Role = rules.Elements[saved.Element].Role, Home = saved.Home, Shield = saved.Shield,
                    AttackRemaining = saved.AttackRemaining, ShieldRemaining = saved.ShieldRemaining,
                    RangeMultiplier = saved.RangeMultiplier, SpeedMultiplier = saved.SpeedMultiplier, Haste = saved.Haste,
                    SmokeDebuff = saved.SmokeDebuff, SteamDebuff = saved.SteamDebuff, SandDebuff = saved.SandDebuff,
                    EcologyNeutral = saved.EcologyNeutral, NeutralReturning = saved.NeutralReturning, CatchingUp = saved.CatchingUp,
                    DetourSide = saved.DetourSide, KillerOwner = saved.KillerOwner, KillerSource = saved.KillerSource,
                    KillerElement = saved.KillerElement, KillerTower = saved.KillerTower
                };
                Check(saved.Statuses != null && saved.Statuses.Length == unit.Statuses.Length &&
                    saved.LastHitTimes != null && saved.LastHitTimes.Length == unit.LastHitTimes.Length &&
                    saved.LastHitOwners != null && saved.LastHitOwners.Length == unit.LastHitOwners.Length, "전투 상태 길이");
                for (int index = 0; index < unit.Statuses.Length; index++)
                {
                    StatusData status = saved.Statuses[index];
                    Check(Finite(status.Expires) && status.Expires >= 0 && Finite(status.Amount) && Finite(status.Dps) && status.Dps >= 0 &&
                        status.Owner >= -1 && status.LegionOwner >= -1 && status.Source >= 0 && status.Source < NextId &&
                        Element(rules, status.Element), "상태 이상");
                    unit.Statuses[index] = new TimedStatus
                    {
                        Expires = status.Expires, Amount = status.Amount, Dps = status.Dps, Owner = status.Owner,
                        Source = status.Source, Element = status.Element, LegionOwner = status.LegionOwner
                    };
                }
                for (int index = 0; index < unit.LastHitTimes.Length; index++)
                {
                    double hit = saved.LastHitTimes[index];
                    Check((double.IsNegativeInfinity(hit) || Finite(hit) && hit >= 0 && hit <= world.Time) &&
                        saved.LastHitOwners[index] >= -1, "공격 귀속");
                    unit.LastHitTimes[index] = hit;
                    unit.LastHitOwners[index] = saved.LastHitOwners[index];
                }
                world.Units.Add(unit);
                world.UnitById.Add(unit.Id, unit);
                if (unit.OwnerId >= 0)
                {
                    PlayerState player = world.PlayerById[unit.OwnerId];
                    player.Population++;
                    unit.Ghost = player.GhostEndTick != 0;
                }
                previous = saved.Id;
            }
            for (int index = 0; index < Units.Length; index++)
            {
                UnitData saved = Units[index];
                UnitState unit = world.Units[index];
                world.UnitById.TryGetValue(saved.Target, out unit.Target);
                world.BuildingById.TryGetValue(saved.BuildingTarget, out unit.BuildingTarget);
                world.BuildingById.TryGetValue(saved.Detour, out unit.Detour);
                if (unit.OwnerId < 0 && unit.EcologyNeutral && saved.Target != 0 && unit.Target == null)
                    unit.NeutralReturning = true;
            }
            foreach (PlayerState player in world.Players)
                Check(player.Population + player.TrainingQueue.Count <= player.PopulationCap, "예약 인구");
        }

        internal static void Check(bool valid, string field)
        {
            if (!valid) throw new ArgumentException("월드 체크포인트 값이 잘못됐습니다. " + field);
        }

        internal static bool Inside(GameRules rules, Vector2 point) =>
            point.X >= 0 && point.Y >= 0 && point.X <= rules.MapSize && point.Y <= rules.MapSize;
        private static bool Element(GameRules rules, int index) => index >= 0 && index < rules.Elements.Count;
        private static bool Debuff(float amount) => amount >= -1 && amount <= 0;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static void ValidateTrainingTimer(WorldState world, PlayerState player)
        {
            bool paused = player.TrainingPausedAtTick >= 0;
            long at = paused ? player.TrainingPausedAtTick : world.Tick;
            Check(player.TrainingPausedAtTick >= -1 && at <= world.Tick &&
                (paused ? player.GhostEndTick != 0 : player.GhostEndTick == 0 || player.TrainingQueue.Count == 0), "양성 정지 상태");
            Check(player.TrainingQueue.Count == 0
                ? player.TrainingStartTick == 0 && player.TrainingEndTick == 0 && !paused
                : player.TrainingStartTick >= 0 && player.TrainingStartTick <= at && player.TrainingEndTick > at &&
                    player.TrainingEndTick - player.TrainingStartTick ==
                        (long)Math.Ceiling(world.Rules.TrainingSeconds(player.TrainingElement, player.MainElement) * world.Rules.TickRate),
                "양성 타이머");
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class PlayerData
        {
            [JsonProperty(Required = Required.Default)] public int? ColorId;
            public string Nickname;
            public int Id, Slot, MainElement, BaseLevel, BattleWins;
            public Vector2 BasePosition, LegionPosition, Heading;
            public long Cores, GhostEndTick, Trained, Converted, GarrisonArrivals;
            public long TrainingStartTick, TrainingEndTick, TrainingPausedAtTick;
            public ulong NextTrainingOrderId;
            public byte[] Explored;
            public TrainingData[] Training;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct TrainingData
        {
            public ulong Id;
            public int Element, Cost;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class UnitData
        {
            public int Id, Owner, Element, Target, BuildingTarget, Detour, DetourSide;
            public int KillerOwner, KillerSource, KillerElement;
            public Vector2 Position, Home;
            public float Health, Shield, RangeMultiplier, SpeedMultiplier, Haste, SmokeDebuff, SteamDebuff, SandDebuff;
            public double AttackRemaining, ShieldRemaining;
            public bool Converted, Garrison, WasNeutral, EcologyNeutral, NeutralReturning, CatchingUp, KillerTower;
            public StatusData[] Statuses;
            public double[] LastHitTimes;
            public int[] LastHitOwners;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct StatusData
        {
            public double Expires;
            public float Amount, Dps;
            public int Owner, Source, Element, LegionOwner;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct BuildingData
        {
            public int Id, Owner, TowerElement, KillerOwner, KillerSource, KillerElement;
            public BuildingKind Kind;
            public Vector2 Position;
            public float Health;
            public double AttackRemaining;
            public long ConstructionEndTick;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct CoreData
        {
            public int Id, Amount;
            public Vector2 Position;
            public double Expires;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct AreaData
        {
            public int Id, Reaction, Owner, Source, Element, Target;
            public Vector2 Position;
            public double Expires;
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal sealed class EcologyData
        {
            public float MinBaseDistance;
            public Vector2[] Sites;
            public GroupData[] Groups;
            public ScheduleData[] Schedules;
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct GroupData
        {
            public int Element, StarterSite;
            public bool Energy;
            public Vector2 Anchor;
            public SlotData[] Slots;
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct SlotData
        {
            public int Unit;
            public long ReadyTick;
        }

        [JsonObject(ItemRequired = Required.Always)]
        internal struct ScheduleData
        {
            public int Owner;
            public long Credit;
            public bool GlobalNext;
        }
    }
}
