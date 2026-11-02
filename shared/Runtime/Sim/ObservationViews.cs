using System;
using System.Numerics;
using ElementalLegion.Shared.Data;

namespace ElementalLegion.Shared.Sim
{
    public enum ObservedEntityKind : byte { Unit, Core, Building, Area }
    public enum BuildingKind : byte { Base, NatureForge, EnergyForge }

    /// <summary>서버가 허용한 건물의 표시·충돌 자료다. M3에는 건물 전투 상태가 없다.</summary>
    public readonly struct BuildingView
    {
        public int Id { get; }
        public int OwnerId { get; }
        public BuildingKind Kind { get; }
        public Vector2 Position { get; }
        public float Radius { get; }
        public BuildingView(int id, int ownerId, BuildingKind kind, Vector2 position, float radius)
        { Id=id; OwnerId=ownerId; Kind=kind; Position=position; Radius=radius; }
        internal BuildingView(BuildingState state) : this(state.Id,state.OwnerId,state.Kind,state.Position,state.Radius) { }
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
