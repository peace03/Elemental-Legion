using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>서버가 확정한 여정 결과의 누적값이다.</summary>
    public readonly struct JourneyTotals
    {
        public long Converted { get; }
        public long Rescued { get; }
        public long Lost { get; }
        public long CoresIn { get; }
        public long CoresSpent { get; }
        public long BuildingsDestroyed { get; }
        public long SiegeWins { get; }
        public JourneyTotals(long converted, long rescued, long lost, long coresIn, long coresSpent, long buildingsDestroyed, long siegeWins)
        {
            if (converted < 0 || rescued < 0 || lost < 0 || coresIn < 0 || coresSpent < 0 || buildingsDestroyed < 0 || siegeWins < 0)
                throw new ArgumentOutOfRangeException(nameof(converted), "여정 결과는 음수일 수 없습니다.");
            Converted = converted; Rescued = rescued; Lost = lost; CoresIn = coresIn; CoresSpent = coresSpent;
            BuildingsDestroyed = buildingsDestroyed; SiegeWins = siegeWins;
        }
        internal JourneyTotals Add(long converted = 0, long rescued = 0, long lost = 0, long coresIn = 0, long coresSpent = 0, long buildingsDestroyed = 0, long siegeWins = 0)
            => new JourneyTotals(Sum(Converted, converted), Sum(Rescued, rescued), Sum(Lost, lost), Sum(CoresIn, coresIn),
                Sum(CoresSpent, coresSpent), Sum(BuildingsDestroyed, buildingsDestroyed), Sum(SiegeWins, siegeWins));
        internal bool Contains(JourneyTotals other) => Converted >= other.Converted && Rescued >= other.Rescued && Lost >= other.Lost &&
            CoresIn >= other.CoresIn && CoresSpent >= other.CoresSpent && BuildingsDestroyed >= other.BuildingsDestroyed && SiegeWins >= other.SiegeWins;
        internal JourneyTotals Since(JourneyTotals acknowledged) => new JourneyTotals(Converted - acknowledged.Converted, Rescued - acknowledged.Rescued,
            Lost - acknowledged.Lost, CoresIn - acknowledged.CoresIn, CoresSpent - acknowledged.CoresSpent,
            BuildingsDestroyed - acknowledged.BuildingsDestroyed, SiegeWins - acknowledged.SiegeWins);
        // 포화 뒤에는 추가 결과를 수치로 구분하지 못하지만 실제 게임 처리는 계속한다.
        private static long Sum(long total, long amount) => total + Math.Min(long.MaxValue - total, amount);
    }

    /// <summary>본인에게만 공개하는 전체 결과와 마지막 확인 이후의 결과다.</summary>
    public readonly struct JourneyResultView
    {
        public long Id { get; }
        public JourneyTotals Total { get; }
        public JourneyTotals SinceLast { get; }
        public JourneyResultView(long id, JourneyTotals total, JourneyTotals sinceLast)
        { Id = id; Total = total; SinceLast = sinceLast; }
    }

    public enum ObservedEntityKind : byte { Unit, Core, Building, Area, Candidate }
    public enum RecoveryRole : byte { Observer, Secure, Rescue }

    /// <summary>현재 시야 안의 후보와 조회자에게 허용된 한쪽 진행만 전달한다.</summary>
    public readonly struct ConversionCandidateView
    {
        public int Id { get; }
        public int Element { get; }
        public Vector2 Position { get; }
        public long EndTick { get; }
        public RecoveryRole Role { get; }
        public int ProgressTicks { get; }
        public ConversionCandidateView(int id, int element, Vector2 position, long endTick, RecoveryRole role, int progressTicks)
        { Id = id; Element = element; Position = position; EndTick = endTick; Role = role; ProgressTicks = progressTicks; }
    }
    public enum BuildingKind : byte { Base, NatureForge, EnergyForge, Collector, NatureTower, EnergyTower, Dome }

    /// <summary>본인에게만 공개하는 분대의 명령과 합류·예약 수량이다.</summary>
    public readonly struct SquadView
    {
        public byte Id { get; }
        public Vector2 Position { get; }
        public Vector2 Direction { get; }
        public Vector2 Heading { get; }
        public Vector2 Destination { get; }
        public SquadOrderKind Order { get; }
        public ulong AppliedSequence { get; }
        public int JoinedCount { get; }
        public int ReinforcingCount { get; }
        public int MemberCount => JoinedCount + ReinforcingCount;
        public int ReservedPopulation { get; }
        public uint MovementRevision { get; }
        public SquadView(byte id, Vector2 position, Vector2 direction, Vector2 heading, Vector2 destination, SquadOrderKind order, ulong appliedSequence, int joinedCount, int reinforcingCount, int reservedPopulation, uint movementRevision = 0)
        {
            Id = id; Position = position; Direction = direction; Heading = heading; Destination = destination; Order = order;
            AppliedSequence = appliedSequence; JoinedCount = joinedCount; ReinforcingCount = reinforcingCount; ReservedPopulation = reservedPopulation; MovementRevision = movementRevision;
        }
    }

    /// <summary>본인이 대기 중인 예약을 취소할 때 사용하는 예약 번호와 원소다.</summary>
    public readonly struct TrainingOrderView
    {
        public ulong Id { get; }
        public int Element { get; }
        public byte TargetSquad { get; }
        public TrainingOrderView(ulong id, int element, byte targetSquad = 4) { Id = id; Element = element; TargetSquad = targetSquad; }
    }

    /// <summary>모든 접속자에게 공개하는 닉네임, 플레이어 ID, 종합 점수와 소유 색이다.</summary>
    public readonly struct PlayerStanding
    {
        public int PlayerId { get; }
        public long Score { get; }
        public string Nickname { get; }
        public int ColorId { get; }
        public PlayerStanding(int playerId, long score, string nickname, int colorId = 0) { PlayerId = playerId; Score = score; Nickname = nickname; ColorId = colorId; }
    }

    /// <summary>공개된 순위 또는 허용된 관측에 필요한 소유 색이다. 위치와 경제는 포함하지 않는다.</summary>
    public readonly struct OwnerColorView
    {
        public int PlayerId { get; }
        public int ColorId { get; }
        public OwnerColorView(int playerId, int colorId) { PlayerId = playerId; ColorId = colorId; }
    }

    /// <summary>서버가 허용한 건물의 표시·충돌 자료다.</summary>
    public readonly struct BuildingView
    {
        public int Id { get; }
        public int OwnerId { get; }
        public BuildingKind Kind { get; }
        public Vector2 Position { get; }
        public float Radius { get; }
        public float Health { get; }
        public float MaxHealth { get; }
        public int TowerElement { get; }
        public long GhostEndTick { get; }
        public long ConstructionEndTick { get; }
        public FacilityUpgradeBranch UpgradeBranch { get; }
        public byte UpgradeRank { get; }
        public long NextProductionTick { get; }
        public bool IsUnderConstruction => ConstructionEndTick != 0;
        public bool BlocksMovement => Kind != BuildingKind.Dome;
        public BuildingView(int id, int ownerId, BuildingKind kind, Vector2 position, float radius)
            : this(id, ownerId, kind, position, radius, 1, 1, -1, 0) { }
        public BuildingView(int id, int ownerId, BuildingKind kind, Vector2 position, float radius, float health, float maxHealth,
            int towerElement, long ghostEndTick, long constructionEndTick = 0,
            FacilityUpgradeBranch upgradeBranch = FacilityUpgradeBranch.None, byte upgradeRank = 0, long nextProductionTick = 0)
        {
            Id = id; OwnerId = ownerId; Kind = kind; Position = position; Radius = radius;
            Health = health; MaxHealth = maxHealth; TowerElement = towerElement; GhostEndTick = ghostEndTick;
            ConstructionEndTick = constructionEndTick;
            UpgradeBranch = upgradeBranch; UpgradeRank = upgradeRank; NextProductionTick = nextProductionTick;
        }
        internal BuildingView(BuildingState state, long ghostEndTick, bool own)
            : this(state.Id,state.OwnerId,state.Kind,state.Position,state.Radius,state.Health,state.MaxHealth,
                state.TowerElement,ghostEndTick,state.ConstructionEndTick,
                own ? state.UpgradeBranch : FacilityUpgradeBranch.None, own ? state.UpgradeRank : (byte)0,
                own && ghostEndTick == 0 && !state.IsUnderConstruction ? state.NextProductionTick : 0) { }
    }

    /// <summary>현재 관측 가능한 고정 반응 영역이다. 숨은 시전자 정보는 포함하지 않는다.</summary>
    public readonly struct AreaView
    {
        public int Id { get; }
        public ReactionKind Kind { get; }
        public Vector2 Position { get; }
        public float Radius { get; }
        public long EndTick { get; }
        public AreaView(int id, ReactionKind kind, Vector2 position, float radius, long endTick)
        { Id=id; Kind=kind; Position=position; Radius=radius; EndTick=endTick; }
        internal AreaView(AreaState state,int tickRate) : this(state.Id,state.Definition.Kind,state.Position,state.Definition.Radius,(long)Math.Ceiling(state.Expires*tickRate)) { }
    }
}
