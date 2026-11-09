using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    public enum ObservedEntityKind : byte { Unit, Core, Building, Area }
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
        public bool IsUnderConstruction => ConstructionEndTick != 0;
        public bool BlocksMovement => Kind != BuildingKind.Dome;
        public BuildingView(int id, int ownerId, BuildingKind kind, Vector2 position, float radius)
            : this(id, ownerId, kind, position, radius, 1, 1, -1, 0) { }
        public BuildingView(int id, int ownerId, BuildingKind kind, Vector2 position, float radius, float health, float maxHealth,
            int towerElement, long ghostEndTick, long constructionEndTick = 0)
        {
            Id = id; OwnerId = ownerId; Kind = kind; Position = position; Radius = radius;
            Health = health; MaxHealth = maxHealth; TowerElement = towerElement; GhostEndTick = ghostEndTick;
            ConstructionEndTick = constructionEndTick;
        }
        internal BuildingView(BuildingState state, long ghostEndTick)
            : this(state.Id,state.OwnerId,state.Kind,state.Position,state.Radius,state.Health,state.MaxHealth,
                state.TowerElement,ghostEndTick,state.ConstructionEndTick) { }
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
