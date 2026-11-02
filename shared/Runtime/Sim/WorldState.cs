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
        internal readonly Random Random;
        internal readonly List<PlayerState> Players = new List<PlayerState>();
        internal readonly Dictionary<int, PlayerState> PlayerById = new Dictionary<int, PlayerState>();
        internal readonly List<UnitState> Units = new List<UnitState>();
        internal readonly Dictionary<int, UnitState> UnitById = new Dictionary<int, UnitState>();
        internal readonly List<BuildingState> Buildings = new List<BuildingState>();
        internal readonly List<CoreState> Cores = new List<CoreState>();
        internal readonly List<SimEvent> Events = new List<SimEvent>();
        internal readonly List<QueuedCommand> Commands = new List<QueuedCommand>();
        internal readonly List<AreaState> Areas = new List<AreaState>();
        internal readonly List<UnitState> Query = new List<UnitState>();
        internal readonly SpatialGrid Grid;
        internal readonly WorldMetrics Metrics = new WorldMetrics();
        internal readonly bool CollectTimings;
        internal int NextId = 1;
        internal int NextBuildingId = 1, NextAreaId = 1;
        internal long Tick;
        internal double Time => Tick * Rules.DeltaTime;
        internal WorldState(GameRules rules, int seed, bool timings)
        { Rules = rules; Random = new Random(seed); CollectTimings = timings; Grid = new SpatialGrid(rules.MapSize, rules.CellSize); }
        internal UnitState AddUnit(int ownerId, int element, Vector2 position, bool converted = false, bool garrison = false, float? health = null)
        {
            var unit = new UnitState(NextId++, ownerId, element, position, Rules.Elements[element].Hp, converted, garrison, ownerId < 0, Rules.Elements.Count)
            { Health = health ?? Rules.Elements[element].Hp, Role = Rules.Elements[element].Role };
            Units.Add(unit); UnitById.Add(unit.Id, unit);
            if (ownerId >= 0) { PlayerById[ownerId].Population++; PlayerById[ownerId].FormationDirty = true; }
            return unit;
        }
    }

    /// <summary>플레이어가 소유한 자원·군단·양성 예약과 명령 수명을 관리한다.</summary>
    internal sealed class PlayerState
    {
        internal int Id, Slot, MainElement, BaseLevel, Population, PopulationCap;
        internal long Cores;
        internal Vector2 BasePosition, LegionPosition, Direction, Heading = Vector2.UnitY;
        internal bool NatureForge, EnergyForge, FormationDirty = true;
        internal int TrainingElement = -1;
        internal double TrainingEnd;
        internal long TrainingStartTick, TrainedCount, ConvertedCount;
        internal readonly HashSet<int> PreviousObservedUnits = new HashSet<int>();
        internal bool Rallying;
        internal ulong LastSequence;
        internal ulong AppliedSequence;
        internal long RateWindow = -1;
        internal int CommandsInWindow;
        internal readonly List<UnitState> Legion = new List<UnitState>();
        internal readonly List<Vector2> VisionSources = new List<Vector2>();
        internal readonly WorldView View = new WorldView();
        internal VisionGrid Vision;
        internal ulong ElementMask;
    }

    /// <summary>공유 원소 정의와 분리된 개별 유닛의 전투 상태다.</summary>
    internal sealed class UnitState
    {
        internal readonly int Id;
        internal int OwnerId, Element;
        internal UnitRole Role;
        internal Vector2 Position, Slot, Home;
        internal float Health, Shield, SpeedMultiplier = 1, RangeMultiplier = 1;
        internal float Haste, SmokeDebuff, SteamDebuff, SandDebuff;
        internal float AreaDamage;
        internal int AreaOwner, AreaSource, AreaElement;
        internal ulong SmokeOwners, VisibleTo;
        internal bool Converted, Garrison, WasNeutral, Dead;
        internal double AttackRemaining, ShieldRemaining;
        internal UnitState Target;
        internal readonly TimedStatus[] Statuses = new TimedStatus[(int)StatusKind.Count];
        internal readonly double[] LastHitTimes;
        internal readonly int[] LastHitOwners;
        internal int KillerOwner = -1, KillerSource, KillerElement;
        internal bool KillerTower;
        internal long ImpactTick=-1;
        internal int ImpactIndex=-1;
        internal UnitState(int id, int owner, int element, Vector2 position, float hp, bool converted, bool garrison, bool neutral, int elementCount)
        {
            Id = id; OwnerId = owner; Element = element; Position = position; Health = hp; Converted = converted; Garrison = garrison; WasNeutral = neutral;
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
    }

    /// <summary>이동을 막고 시야를 제공하는 사전 배치 건물이다.</summary>
    internal readonly struct BuildingState
    {
        internal readonly int Id;
        internal readonly BuildingKind Kind;
        internal readonly int OwnerId;
        internal readonly Vector2 Position;
        internal readonly float Radius;
        internal BuildingState(int id, int owner, BuildingKind kind, Vector2 position, float radius) { Id=id; Kind=kind; OwnerId = owner; Position = position; Radius = radius; }
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
