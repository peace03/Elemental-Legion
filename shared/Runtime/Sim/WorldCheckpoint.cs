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
                SchemaVersion = 3, DataHash = world.Rules.DataHash, Tick = world.Tick, RoomPlayers = world.Rules.RoomPlayers, Seed = world.Seed,
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
                    orders[order] = new TrainingData { Id = item.Id, Element = item.Element, Cost = item.Cost, TargetSquad = item.TargetSquad };
                }
                snapshot.Players[index] = new PlayerData
                {
                    Id = player.Id, Nickname = player.Nickname, ColorId = player.ColorId, Slot = player.Slot, MainElement = player.MainElement,
                    BaseLevel = player.BaseLevel, BasePosition = player.BasePosition, LegionPosition = player.LegionPosition,
                    Heading = player.Heading, Cores = player.Cores, GhostEndTick = player.GhostEndTick,
                    BattleWins = player.BattleWins, Trained = player.TrainedCount, Converted = player.ConvertedCount,
                    GarrisonArrivals = player.GarrisonArrivals, Explored = player.Vision.Explored.ToArray(), Training = orders,
                    TrainingStartTick = player.TrainingStartTick, TrainingEndTick = player.TrainingEndTick,
                    TrainingPausedAtTick = player.TrainingPausedAtTick, NextTrainingOrderId = player.NextTrainingOrderId,
                    NextDefenseAlarmTick = player.NextDefenseAlarmTick,
                    FocusAnchor = player.FocusAnchor, Squads = CaptureSquads(player),
                    RecallSquad = player.RecallSquad, RecallEndTick = player.RecallEndTick, RecallReadyTick = player.RecallReadyTick,
                    RecallSequence = player.RecallSequence, RecallArrivalSlots = CopyRecallSlots(player)
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
                        Source = value.Source, Element = value.Element, LegionOwner = value.LegionOwner, SourceSquad = value.SourceSquad
                    };
                }
                snapshot.Units[index] = new UnitData
                {
                    Id = unit.Id, Owner = unit.OwnerId, Element = unit.Element, Position = unit.Position, Home = unit.Home,
                    Health = unit.Health, Shield = unit.Shield, AttackRemaining = unit.AttackRemaining,
                    ShieldRemaining = unit.ShieldRemaining, RangeMultiplier = unit.RangeMultiplier, SpeedMultiplier = unit.SpeedMultiplier,
                    Haste = unit.Haste, SmokeDebuff = unit.SmokeDebuff, SteamDebuff = unit.SteamDebuff, SandDebuff = unit.SandDebuff,
                    Converted = unit.Converted, Garrison = unit.Garrison, WasNeutral = unit.WasNeutral, FreeOrigin = unit.FreeOrigin,
                    EcologyNeutral = unit.EcologyNeutral, NeutralReturning = unit.NeutralReturning, CatchingUp = unit.CatchingUp,
                    Target = unit.Target?.Id ?? 0, BuildingTarget = unit.BuildingTarget?.Id ?? 0,
                    Detour = unit.Detour?.Id ?? 0, DetourSide = unit.DetourSide,
                    Statuses = statuses, LastHitTimes = (double[])unit.LastHitTimes.Clone(),
                    LastHitOwners = (int[])unit.LastHitOwners.Clone(),
                    KillerOwner = unit.KillerOwner, KillerSource = unit.KillerSource, KillerElement = unit.KillerElement,
                    KillerTower = unit.KillerTower, KillerSquad = unit.KillerSquad,
                    SquadId = unit.SquadId, DestinationSquadId = unit.DestinationSquadId, IsReinforcing = unit.IsReinforcing, MovementRevision = unit.MovementRevision,
                    LastHitSquads = (byte[])unit.LastHitSquads.Clone()
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
                    KillerSource = building.KillerSource, KillerElement = building.KillerElement, KillerSquad = building.KillerSquad, FreeOrigin = building.FreeOrigin
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
                    Source = area.Source, SourceSquad = area.SourceSquad, Element = area.Element, Target = area.Target, Position = area.Position, Expires = area.Expires
                };
            }
            return snapshot;
        }

        internal WorldState Restore(GameRules rules, bool collectTimings)
        {
            Check(SchemaVersion == 3, "월드 스키마 버전");
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
            RestoreRecall(world);
            foreach (PlayerState player in world.Players)
                foreach (BuildingState building in world.Buildings)
                    Check(!building.BlocksMovement || Vector2.DistanceSquared(player.Squads[3].Position, building.Position) >=
                        building.Radius * building.Radius - 0.001f, "수비대 중심 충돌");
            RestoreUnits(world);
            for (int player = 0; player < Players.Length; player++)
                for (int squad = 0; squad < 4; squad++)
                    world.Players[player].Squads[squad].FormationDirty = Players[player].Squads[squad].FormationDirty;
            foreach (PlayerState player in world.Players)
                if (player.TrainingQueue.Count > 0)
                    Check(world.TryReserveUnitSlots(player.TrainingQueue.Count), "양성 전역 예약 용량");

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
                    Finite(saved.Expires) && saved.Expires > world.Time - rules.DeltaTime && saved.SourceSquad >= 1 && saved.SourceSquad <= 3, "반응 영역");
                ReactionDefinition reaction = rules.Reactions[saved.Reaction];
                Check(reaction.Kind == ReactionKind.Steam || reaction.Kind == ReactionKind.Sandstorm, "영역 반응 종류");
                world.Areas.Add(new AreaState
                {
                    Id = saved.Id, Definition = reaction, Owner = saved.Owner, Source = saved.Source, SourceSquad = saved.SourceSquad,
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
                    saved.NextDefenseAlarmTick >= 0 && saved.NextDefenseAlarmTick <= Tick + rules.DefenseAlarmRepeatTicks &&
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
                    Cores = saved.Cores, GhostEndTick = saved.GhostEndTick, BattleWins = saved.BattleWins, NextDefenseAlarmTick = saved.NextDefenseAlarmTick,
                    TrainedCount = saved.Trained, ConvertedCount = saved.Converted, GarrisonArrivals = saved.GarrisonArrivals,
                    PopulationCap = rules.PopulationCap(saved.BaseLevel), Vision = new VisionGrid(rules.MapSize, rules.CellSize)
                };
                RestoreSquads(world, player, saved);
                Check(player.Vision.TryRestoreExplored(saved.Explored), "탐색 비트맵");
                Check(saved.Training != null && saved.Training.Length <= player.PopulationCap && saved.NextTrainingOrderId > 0,
                    "양성 대기열");
                ulong order = 0;
                foreach (TrainingData item in saved.Training)
                {
                    Check(item.Id > order && item.Id < saved.NextTrainingOrderId && Element(rules, item.Element) &&
                        item.Cost == rules.TrainingCost(item.Element, saved.MainElement) && item.TargetSquad >= 1 && item.TargetSquad <= 4, "양성 예약");
                    player.TrainingQueue.Add((item.Id, item.Element, item.Cost, item.TargetSquad));
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

        private static Vector2[] CopyRecallSlots(PlayerState player)
        {
            var slots = new Vector2[player.RecallArrivalSlots.Count];
            player.RecallArrivalSlots.CopyTo(slots, 0);
            return slots;
        }

        private void RestoreRecall(WorldState world)
        {
            for (int i = 0; i < Players.Length; i++)
            {
                PlayerData saved = Players[i];
                PlayerState player = world.Players[i];
                Vector2[] slots;
                Check(saved.RecallSquad.HasValue && saved.RecallEndTick.HasValue && saved.RecallReadyTick.HasValue &&
                    saved.RecallSequence.HasValue, "귀환 상태 누락");
                slots = saved.RecallArrivalSlots;
                Check(RecallSystem.ValidLayout(world, player.BasePosition, slots), "귀환 구역");
                player.RecallSquad = saved.RecallSquad.Value; player.RecallEndTick = saved.RecallEndTick.Value;
                player.RecallReadyTick = saved.RecallReadyTick.Value; player.RecallSequence = saved.RecallSequence.Value;
                Check(player.RecallSquad <= 3 && player.RecallReadyTick >= 0 &&
                    player.RecallReadyTick <= world.Tick + world.Rules.RecallCooldownTicks &&
                    (player.RecallSquad == 0 ? player.RecallEndTick == 0 && player.RecallSequence == 0 :
                        player.RecallSequence > 0 && player.GhostEndTick == 0 && player.RecallReadyTick <= world.Tick &&
                        player.RecallEndTick > world.Tick && player.RecallEndTick <= world.Tick + world.Rules.RecallCastTicks), "귀환 타이머");
                if (player.RecallSquad != 0)
                {
                    SquadState squad = player.Squads[player.RecallSquad - 1];
                    Check(squad.Direction == Vector2.Zero && squad.Order == SquadOrderKind.Hold, "시전 분대 이동");
                }
                slots = (Vector2[])slots.Clone();
                player.RecallArrivalSlots = Array.AsReadOnly(slots);
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
                    saved.KillerOwner >= -1 && saved.KillerSquad <= 3 && saved.KillerSource >= 0 && saved.KillerSource < NextId && Element(rules, saved.KillerElement),
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
                    KillerOwner = saved.KillerOwner, KillerSource = saved.KillerSource, KillerElement = saved.KillerElement, KillerSquad = saved.KillerSquad,
                    FreeOrigin = saved.FreeOrigin
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
                    KillerElement = saved.KillerElement, KillerTower = saved.KillerTower, KillerSquad = saved.KillerSquad, FreeOrigin = saved.FreeOrigin,
                    SquadId = saved.SquadId, DestinationSquadId = saved.DestinationSquadId, IsReinforcing = saved.IsReinforcing, MovementRevision = saved.MovementRevision
                };
                Check(saved.Statuses != null && saved.Statuses.Length == unit.Statuses.Length &&
                    saved.LastHitTimes != null && saved.LastHitTimes.Length == unit.LastHitTimes.Length &&
                    saved.LastHitOwners != null && saved.LastHitOwners.Length == unit.LastHitOwners.Length &&
                    saved.LastHitSquads != null && saved.LastHitSquads.Length == unit.LastHitSquads.Length, "전투 상태 길이");
                Check(saved.KillerSquad <= 3 && (unit.OwnerId < 0
                    ? unit.SquadId == 0 && !unit.IsReinforcing && unit.DestinationSquadId == 0
                    : unit.IsReinforcing ? unit.SquadId == 0 && unit.DestinationSquadId >= 1 && unit.DestinationSquadId <= 3
                    : unit.SquadId >= 1 && unit.SquadId <= 4 && unit.DestinationSquadId == 0 && unit.Garrison == (unit.SquadId == 4)), "분대 소속");
                for (int index = 0; index < unit.Statuses.Length; index++)
                {
                    StatusData status = saved.Statuses[index];
                    Check(Finite(status.Expires) && status.Expires >= 0 && Finite(status.Amount) && Finite(status.Dps) && status.Dps >= 0 &&
                        status.Owner >= -1 && status.LegionOwner >= -1 && status.SourceSquad <= 3 &&
                        (status.Expires <= world.Time || (status.SourceSquad == 0 ? status.LegionOwner < 0 : status.LegionOwner == status.Owner)) && status.Source >= 0 && status.Source < NextId &&
                        Element(rules, status.Element), "상태 이상");
                    unit.Statuses[index] = new TimedStatus
                    {
                        Expires = status.Expires, Amount = status.Amount, Dps = status.Dps, Owner = status.Owner,
                        Source = status.Source, Element = status.Element, LegionOwner = status.LegionOwner, SourceSquad = status.SourceSquad
                    };
                }
                for (int index = 0; index < unit.LastHitTimes.Length; index++)
                {
                    double hit = saved.LastHitTimes[index];
                    Check((double.IsNegativeInfinity(hit) || Finite(hit) && hit >= 0 && hit <= world.Time) &&
                        saved.LastHitOwners[index] >= -1 && saved.LastHitSquads[index] <= 3 &&
                        (saved.LastHitOwners[index] < 0 ? saved.LastHitSquads[index] == 0 : saved.LastHitSquads[index] >= 1), "공격 귀속");
                    unit.LastHitTimes[index] = hit;
                    unit.LastHitOwners[index] = saved.LastHitOwners[index];
                    unit.LastHitSquads[index] = saved.LastHitSquads[index];
                }
                world.InsertUnit(unit);
                if (unit.OwnerId >= 0)
                {
                    PlayerState player = world.PlayerById[unit.OwnerId];
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
            {
                Check(player.Population + player.TrainingQueue.Count <= player.PopulationCap, "예약 인구");
                Check(TrainingSystem.WaitingGarrison(world, player) + TrainingSystem.ReservedFor(player, 4) <= rules.GarrisonCap(player.BaseLevel), "수비대 예약 인구");
            }
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

        private static SquadData[] CaptureSquads(PlayerState player)
        {
            var result = new SquadData[4];
            for (int i = 0; i < result.Length; i++)
            {
                SquadState squad = player.Squads[i];
                result[i] = new SquadData { Id = squad.Id, Position = squad.Position, Direction = squad.Direction, Heading = squad.Heading,
                    Destination = squad.Destination, Order = squad.Order, AppliedSequence = squad.AppliedSequence,
                    ElementMask = squad.ElementMask, MovementRevision = squad.MovementRevision, FormationDirty = squad.FormationDirty,
                    FollowFormation = squad.FollowFormation };
            }
            return result;
        }

        private static void RestoreSquads(WorldState world, PlayerState player, PlayerData saved)
        {
            Check(saved.FocusAnchor >= 1 && saved.FocusAnchor <= 4 && saved.Squads != null && saved.Squads.Length == 4, "분대 목록");
            player.FocusAnchor = saved.FocusAnchor;
            for (int i = 0; i < 4; i++)
            {
                SquadData data = saved.Squads[i];
                Check(data != null && data.Id == i + 1 && Inside(world.Rules, data.Position) && Inside(world.Rules, data.Destination) &&
                    Finite(data.Direction.X) && Finite(data.Direction.Y) && data.Direction.LengthSquared() <= 1.001f &&
                    Finite(data.Heading.X) && Finite(data.Heading.Y) && data.Heading.LengthSquared() <= 1.001f &&
                    data.Order <= SquadOrderKind.MoveTo && (data.ElementMask >> world.Rules.Elements.Count) == 0 &&
                    (i == 3 ? data.Order == SquadOrderKind.Hold || data.FollowFormation : !data.FollowFormation) &&
                    (data.Order != SquadOrderKind.Hold || data.Direction == Vector2.Zero) &&
                    (i != 3 || Vector2.DistanceSquared(data.Position, player.BasePosition) <= world.Rules.GarrisonRadius * world.Rules.GarrisonRadius + 0.001f &&
                        Vector2.DistanceSquared(data.Destination, player.BasePosition) <= world.Rules.GarrisonRadius * world.Rules.GarrisonRadius + 0.001f), "분대 상태");
                SquadState squad = player.Squads[i];
                squad.Position = data.Position; squad.Direction = data.Direction; squad.Heading = data.Heading; squad.Destination = data.Destination;
                squad.Order = data.Order; squad.AppliedSequence = data.AppliedSequence; squad.ElementMask = data.ElementMask;
                squad.MovementRevision = data.MovementRevision; squad.FormationDirty = data.FormationDirty;
                squad.FollowFormation = data.FollowFormation;
            }
            Check(saved.LegionPosition == player.LegionPosition && saved.Heading == player.Heading, "1번 분대 별칭");
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class SquadData
        {
            public byte Id;
            public Vector2 Position, Direction, Heading, Destination;
            public SquadOrderKind Order;
            public ulong AppliedSequence, ElementMask;
            public uint MovementRevision;
            public bool FormationDirty, FollowFormation;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class PlayerData
        {
            [JsonProperty(Required = Required.Default)] public byte? RecallSquad;
            [JsonProperty(Required = Required.Default)] public long? RecallEndTick, RecallReadyTick;
            [JsonProperty(Required = Required.Default)] public ulong? RecallSequence;
            [JsonProperty(Required = Required.Default)] public Vector2[] RecallArrivalSlots;
            [JsonProperty(Required = Required.Default)] public int? ColorId;
            public string Nickname;
            public int Id, Slot, MainElement, BaseLevel, BattleWins;
            public Vector2 BasePosition, LegionPosition, Heading;
            public long Cores, GhostEndTick, Trained, Converted, GarrisonArrivals;
            public long TrainingStartTick, TrainingEndTick, TrainingPausedAtTick, NextDefenseAlarmTick;
            public ulong NextTrainingOrderId;
            public byte[] Explored;
            public TrainingData[] Training;
            public byte FocusAnchor;
            public SquadData[] Squads;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct TrainingData
        {
            public ulong Id;
            public int Element, Cost;
            public byte TargetSquad;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private sealed class UnitData
        {
            [JsonProperty(Required = Required.Default)] public bool FreeOrigin;
            public int Id, Owner, Element, Target, BuildingTarget, Detour, DetourSide;
            public int KillerOwner, KillerSource, KillerElement;
            public Vector2 Position, Home;
            public float Health, Shield, RangeMultiplier, SpeedMultiplier, Haste, SmokeDebuff, SteamDebuff, SandDebuff;
            public double AttackRemaining, ShieldRemaining;
            public bool Converted, Garrison, WasNeutral, EcologyNeutral, NeutralReturning, CatchingUp, KillerTower;
            public byte SquadId, DestinationSquadId, KillerSquad;
            public bool IsReinforcing;
            public uint MovementRevision;
            public byte[] LastHitSquads;
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
            public byte SourceSquad;
        }

        [JsonObject(ItemRequired = Required.Always)]
        private struct BuildingData
        {
            [JsonProperty(Required = Required.Default)] public bool FreeOrigin;
            public int Id, Owner, TowerElement, KillerOwner, KillerSource, KillerElement;
            public byte KillerSquad;
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
            public byte SourceSquad;
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
            [JsonProperty(Required = Required.Default)] public bool? Reserved;
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
