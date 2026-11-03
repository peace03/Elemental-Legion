using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    public enum ObservedEntityKind : byte { Unit, Core, Building, Area }
    public enum BuildingKind : byte { Base, NatureForge, EnergyForge, Collector, NatureTower, EnergyTower, Dome }

    /// <summary>본인이 대기 중인 예약을 취소할 때 사용하는 예약 번호와 원소다.</summary>
    public readonly struct TrainingOrderView
    {
        public ulong Id { get; }
        public int Element { get; }
        public TrainingOrderView(ulong id, int element) { Id = id; Element = element; }
    }

    /// <summary>모든 접속자에게 공개하는 닉네임, 플레이어 ID와 종합 점수다.</summary>
    public readonly struct PlayerStanding
    {
        public int PlayerId { get; }
        public long Score { get; }
        public string Nickname { get; }
        public PlayerStanding(int playerId, long score, string nickname) { PlayerId = playerId; Score = score; Nickname = nickname; }
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
