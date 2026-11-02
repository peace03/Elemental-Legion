using System.Collections.Generic;
using System.Numerics;

namespace ElementalLegion.Shared.Sim
{
    /// <summary>서버가 생성하는 초기 상태다. 생성자는 이 자료를 검증·복사하므로 이후 수정이 월드에 반영되지 않는다.</summary>
    public sealed class WorldSetup
    {
        public List<PlayerSpawn> Players { get; } = new List<PlayerSpawn>();
        public List<UnitSpawn> Units { get; } = new List<UnitSpawn>();
        public List<CoreSpawn> Cores { get; } = new List<CoreSpawn>();
        public bool CollectTimings { get; set; }
    }

    /// <summary>사전 배치된 본진·군단·양성소 설정을 전달한다. 클라이언트 명령이 아니다.</summary>
    public sealed class PlayerSpawn
    {
        public int Id { get; }
        public string MainElement { get; }
        public Vector2 BasePosition { get; }
        public Vector2 LegionPosition { get; }
        public int BaseLevel { get; }
        public long Cores { get; }
        public bool NatureForge { get; }
        public bool EnergyForge { get; }
        public PlayerSpawn(int id, string mainElement, Vector2 basePosition, Vector2 legionPosition, int baseLevel = 1, long cores = 0, bool natureForge = false, bool energyForge = false)
        {
            Id = id; MainElement = mainElement; BasePosition = basePosition; LegionPosition = legionPosition;
            BaseLevel = baseLevel; Cores = cores; NatureForge = natureForge; EnergyForge = energyForge;
        }
    }

    /// <summary>초기 유닛이다. 체력 지정은 손상 상태 복원과 규칙 테스트에도 사용한다.</summary>
    public sealed class UnitSpawn
    {
        public int OwnerId { get; }
        public string Element { get; }
        public Vector2 Position { get; }
        public float? Health { get; }
        public bool Converted { get; }
        public bool Garrison { get; }
        public UnitSpawn(int ownerId, string element, Vector2 position, float? health = null, bool converted = false, bool garrison = false)
        {
            OwnerId = ownerId; Element = element; Position = position; Health = health; Converted = converted; Garrison = garrison;
        }
    }

    /// <summary>초기 핵의 위치와 수량이다. 수량은 정수이며 월드가 한 번만 지급한다.</summary>
    public readonly struct CoreSpawn
    {
        public Vector2 Position { get; }
        public int Amount { get; }
        public CoreSpawn(Vector2 position, int amount) { Position = position; Amount = amount; }
    }
}
