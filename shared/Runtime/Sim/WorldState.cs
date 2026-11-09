using System;
using System.Collections.Generic;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>시스템들이 한 스레드에서 수정하는 월드의 내부 저장소다.</summary>
    internal sealed class WorldState
    {
        internal readonly GameRules Rules;
        internal readonly int Seed;
        internal readonly StatefulRandom Random;
        internal readonly List<PlayerState> Players = new List<PlayerState>();
        internal readonly Dictionary<int, PlayerState> PlayerById = new Dictionary<int, PlayerState>();
        internal readonly List<UnitState> Units = new List<UnitState>();
        internal readonly Dictionary<int, UnitState> UnitById = new Dictionary<int, UnitState>();
        internal readonly List<BuildingState> Buildings = new List<BuildingState>();
        internal readonly Dictionary<int, BuildingState> BuildingById = new Dictionary<int, BuildingState>();
        internal readonly List<CoreState> Cores = new List<CoreState>();
        internal readonly List<SimEvent> Events = new List<SimEvent>();
        internal readonly List<QueuedCommand> Commands = new List<QueuedCommand>();
        internal readonly List<AreaState> Areas = new List<AreaState>();
        internal readonly List<UnitState> Query = new List<UnitState>();
        internal readonly List<PlayerStanding> Standings = new List<PlayerStanding>();
        internal readonly System.Collections.ObjectModel.ReadOnlyCollection<PlayerStanding> StandingView;
        internal readonly SpatialGrid Grid;
        internal readonly WorldMetrics Metrics = new WorldMetrics();
        internal readonly bool CollectTimings;
        internal bool CameraVisibility;
        internal int NextId = 1;
        internal int NextBuildingId = 1, NextAreaId = 1;
        internal long Tick;
        internal int GhostCount;
        internal int ReservedUnitSlots { get; private set; }
        internal int AvailableUnitSlots => Net.Protocol.MaxUnits - Units.Count - ReservedUnitSlots;

        /// <summary>작업이 소유할 슬롯을 확보한다. 호출자는 완료·취소 때 정확히 한 번 소비하거나 반환한다.</summary>
        internal bool TryReserveUnitSlots(int count)
        {
            if (count <= 0 || count > Net.Protocol.MaxUnits) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > AvailableUnitSlots) return false;
            ReservedUnitSlots += count;
            return true;
        }

        internal void ReleaseUnitSlots(int count)
        {
            if (count <= 0 || count > ReservedUnitSlots) throw new ArgumentOutOfRangeException(nameof(count));
            ReservedUnitSlots -= count;
        }
        internal bool ProtectionEnabled;
        internal double Time => Tick * Rules.DeltaTime;
        internal WorldState(GameRules rules, int seed, bool timings, long? restoredTick = null)
        {
            Rules = rules; Seed = seed; Tick = restoredTick ?? 0; CollectTimings = timings; Grid = new SpatialGrid(rules.MapSize, rules.CellSize);
            // 재시작 후 난수는 저장한 시드와 틱에서 새로 시작한다.
            int randomSeed = restoredTick.HasValue ? unchecked(((seed * 397) ^ (int)Tick) * 397 ^ (int)(Tick >> 32)) : seed;
            Random = new StatefulRandom(randomSeed);
            StandingView = Standings.AsReadOnly();
        }
        internal UnitState AddUnit(int ownerId, int element, Vector2 position, bool converted = false, bool garrison = false, float? health = null,
            bool consumeReservation = false, bool freeOrigin = false)
        {
            if (consumeReservation ? ReservedUnitSlots == 0 : AvailableUnitSlots == 0)
                throw new ArgumentException("월드 유닛 상한을 넘었습니다.");
            var unit = new UnitState(NextId++, ownerId, element, position, Rules.Elements[element].Hp, converted, garrison, ownerId < 0, Rules.Elements.Count)
            { Health = health ?? Rules.Elements[element].Hp, Role = Rules.Elements[element].Role, FreeOrigin = freeOrigin };
            InsertUnit(unit, consumeReservation);
            return unit;
        }

        internal void InsertUnit(UnitState unit, bool consumeReservation = false)
        {
            if (consumeReservation) ReleaseUnitSlots(1);
            else if (AvailableUnitSlots == 0) throw new ArgumentException("월드 유닛 상한을 넘었습니다.");
            Units.Add(unit); UnitById.Add(unit.Id, unit);
            if (unit.OwnerId >= 0) { PlayerById[unit.OwnerId].Population++; PlayerById[unit.OwnerId].FormationDirty = true; }
        }
        internal void AddBuilding(PlayerState player, BuildingKind kind, Vector2 position, int towerElement = -1, long constructionEndTick = 0, bool freeOrigin = false)
        {
            var building = new BuildingState(NextBuildingId++, player.Id, kind, position, Rules, towerElement)
            {
                ConstructionEndTick = constructionEndTick, FreeOrigin = freeOrigin
            };
            Buildings.Add(building);
            BuildingById.Add(building.Id, building);
            if (!building.IsUnderConstruction) ActivateBuilding(player, building);
        }

        internal static void ActivateBuilding(PlayerState player, BuildingState building)
        {
            building.ConstructionEndTick = 0;
            if (building.Kind != BuildingKind.Base) player.BuildingCount++;
            if (building.Kind == BuildingKind.NatureForge) player.NatureForge = true;
            if (building.Kind == BuildingKind.EnergyForge) player.EnergyForge = true;
            if (building.Kind == BuildingKind.Dome) player.Dome = building;
        }
    }

    /// <summary>플레이어가 소유한 자원·군단·양성 예약과 명령 수명을 관리한다.</summary>
    internal sealed class PlayerState
    {
        internal string Nickname;
        internal int Id, Slot, ColorId, MainElement, BaseLevel, Population, PopulationCap;
        internal long Cores;
        internal long GhostEndTick, ProtectionEndTick, NextDefenseAlarmTick;
        internal Vector2 BasePosition;
        internal readonly SquadState[] Squads = { new SquadState(1), new SquadState(2), new SquadState(3), new SquadState(4) };
        internal byte FocusAnchor = 1;
        internal byte RecallSquad;
        internal long RecallEndTick, RecallReadyTick;
        internal ulong RecallSequence;
        internal System.Collections.ObjectModel.ReadOnlyCollection<Vector2> RecallArrivalSlots;
        internal Vector2 LegionPosition { get => Squads[0].Position; set => Squads[0].Position = value; }
        internal Vector2 Direction { get => Squads[0].Direction; set => Squads[0].Direction = value; }
        internal Vector2 Heading { get => Squads[0].Heading; set => Squads[0].Heading = value; }
        internal bool FormationDirty { get => Squads[0].FormationDirty; set { foreach (var squad in Squads) squad.FormationDirty = value; } }
        internal bool NatureForge, EnergyForge;
        internal bool BaseDestroyed;
        internal int BuildingCount, BattleWins;
        internal readonly List<(ulong Id, int Element, int Cost)> TrainingQueue = new List<(ulong Id, int Element, int Cost)>();
        internal int TrainingElement => TrainingQueue.Count == 0 ? -1 : TrainingQueue[0].Element;
        internal BuildingState Dome;
        internal long TrainingStartTick, TrainingEndTick, TrainedCount, ConvertedCount, GarrisonArrivals;
        internal long TrainingPausedAtTick = -1;
        internal ulong NextTrainingOrderId = 1;
        internal readonly HashSet<int> PreviousObservedUnits = new HashSet<int>();
        internal readonly HashSet<int> PreviousObservedBuildings = new HashSet<int>();
        internal int RallyThroughUnitId;
        internal bool Rallies(UnitState unit) => unit.IsReinforcing;
        internal ulong LastSequence;
        internal ulong AppliedSequence { get => Squads[0].AppliedSequence; set => Squads[0].AppliedSequence = value; }
        internal long RateWindow = -1;
        internal int CommandsInWindow;
        internal List<UnitState> Legion => Squads[0].Units;
        internal readonly List<UnitState> OwnedUnits = new List<UnitState>();
        internal readonly List<Vector2> VisionSources = new List<Vector2>();
        internal readonly WorldView View = new WorldView();
        internal VisionGrid Vision;
        internal ulong ElementMask { get => Squads[0].ElementMask; set => Squads[0].ElementMask = value; }
    }

    /// <summary>공유 원소 정의와 분리된 개별 유닛의 전투 상태다.</summary>
    internal sealed class UnitState
    {
        internal readonly int Id;
        internal int OwnerId, Element;
        internal UnitRole Role;
        internal Vector2 Position, Slot, Home;
        internal Vector2 MoveStart, Separation;
        internal float MoveStep;
        internal BuildingState Detour;
        internal int DetourSide;
        internal float Health, Shield, SpeedMultiplier = 1, RangeMultiplier = 1;
        internal float Haste, SmokeDebuff, SteamDebuff, SandDebuff;
        internal float AreaDamage;
        internal int AreaOwner, AreaSource, AreaElement;
        internal ulong SmokeOwners, VisibleTo;
        internal bool Converted, Garrison, WasNeutral, Dead, CatchingUp, Ghost, Protected;
        internal bool FreeOrigin;
        internal byte SquadId, DestinationSquadId;
        internal bool IsReinforcing;
        internal uint MovementRevision;
        internal bool EcologyNeutral, NeutralReturning;
        internal double AttackRemaining, ShieldRemaining;
        internal UnitState Target;
        internal BuildingState BuildingTarget;
        internal readonly TimedStatus[] Statuses = new TimedStatus[(int)StatusKind.Count];
        internal readonly double[] LastHitTimes;
        internal readonly int[] LastHitOwners;
        internal readonly byte[] LastHitSquads;
        internal byte KillerSquad, AreaSquad;
        internal ulong SmokeOwners2, SmokeOwners3;
        internal int KillerOwner = -1, KillerSource, KillerElement;
        internal bool KillerTower;
        internal long ImpactTick=-1;
        internal int ImpactIndex=-1;
        internal UnitState(int id, int owner, int element, Vector2 position, float hp, bool converted, bool garrison, bool neutral, int elementCount)
        {
            Id = id; OwnerId = owner; Element = element; Position = position; Health = hp; Converted = converted; Garrison = garrison; WasNeutral = neutral;
            SquadId = owner < 0 ? (byte)0 : garrison ? (byte)4 : (byte)1;
            LastHitSquads = new byte[elementCount];
            LastHitTimes = new double[elementCount]; LastHitOwners = new int[elementCount];
            for (int i = 0; i < elementCount; i++) { LastHitTimes[i] = double.NegativeInfinity; LastHitOwners[i] = -1; }
        }
    }

    /// <summary>상태의 만료·효과·부여 당시 피해 귀속을 저장한다.</summary>
    internal struct TimedStatus
    {
        internal double Expires;
        internal float Amount, Dps;
        internal int Owner, Source, Element, LegionOwner;
        internal byte SourceSquad;
    }

    /// <summary>건물의 충돌·체력과 타워 공격 주기를 보존한다.</summary>
    internal sealed class BuildingState
    {
        internal readonly int Id;
        internal readonly BuildingKind Kind;
        internal readonly int OwnerId;
        internal readonly Vector2 Position;
        internal readonly float Radius;
        internal readonly float MaxHealth;
        internal readonly int TowerElement;
        internal float Health;
        internal double AttackRemaining;
        internal long ConstructionEndTick;
        internal bool FreeOrigin;
        internal bool IsUnderConstruction => ConstructionEndTick != 0;
        internal int KillerOwner = -1, KillerSource, KillerElement;
        internal byte KillerSquad;
        internal bool BlocksMovement => Kind != BuildingKind.Dome;
        internal BuildingState(int id, int owner, BuildingKind kind, Vector2 position, GameRules rules, int towerElement)
        {
            Id = id; Kind = kind; OwnerId = owner; Position = position; TowerElement = towerElement;
            Radius = rules.BuildingRadius(kind);
            Health = MaxHealth = rules.BuildingHealth(kind);
        }
    }

    /// <summary>핵은 소유자가 없는 월드 개체이며 지급 즉시 제거한다.</summary>
    internal sealed class CoreState
    {
        internal int Id, Amount;
        internal Vector2 Position;
        internal double Expires;
    }

    /// <summary>반응이 만든 고정 영역이다. 같은 반응·발동 대상·소유자는 갱신해 중첩 폭증을 막는다.</summary>
    internal sealed class AreaState
    {
        internal int Id;
        internal ReactionDefinition Definition;
        internal int Owner, Source, Element, Target;
        internal byte SourceSquad;
        internal Vector2 Position;
        internal double Expires;
    }

    /// <summary>인증된 소유자와 명령을 한 틱의 순서로 묶는다.</summary>
    internal readonly struct QueuedCommand
    {
        internal readonly int Player;
        internal readonly SimCommand Command;
        internal QueuedCommand(int player, SimCommand command) { Player = player; Command = command; }
    }
}
