using System.Numerics;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;

namespace ElementalLegion.Server;

/// <summary>접속 소유자·출발점을 서버가 정하고 고정 틱 월드에 명령을 전달한다.</summary>
public sealed class NetworkWorld
{
    private readonly GameRules rules;
    private readonly NetworkSettings settings;
    private readonly HashSet<int> connected = new();
    private int nextOwner = 1;
    private readonly bool demo;
    public SimulationWorld World { get; }
    public int ConnectedCount => connected.Count;

    public NetworkWorld(GameRules rules, NetworkSettings settings,string scenario="normal")
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if(scenario!="normal"&&scenario!="m3-demo")throw new ArgumentException("알 수 없는 서버 시나리오입니다.",nameof(scenario));
        demo=scenario=="m3-demo";
        var setup=new WorldSetup();
        if(demo)for(int slot=0;slot<rules.MaxPlayers;slot++)
        {
            Vector2 origin=BasePosition(slot);
            var first=origin+new Vector2(0,rules.VisionRadius*2);
            var second=origin+new Vector2(rules.VisionRadius/2,rules.VisionRadius*2.5f);
            if(first.X>rules.MapSize||first.Y>rules.MapSize||second.X>rules.MapSize||second.Y>rules.MapSize)throw new ArgumentException("데모 중립 배치가 맵 밖입니다.");
            setup.Units.Add(new UnitSpawn(-1,"fire",first));setup.Units.Add(new UnitSpawn(-1,"fire",second,converted:true));
        }
        World = new SimulationWorld(rules, setup, 1);
    }

    public JoinFailure TryJoin(int element, out int playerId)
    {
        playerId = -1;
        if (!rules.StartingElements.Contains(element)) return JoinFailure.InvalidElement;
        // M4의 유령 만료·M5의 계정 복원 전에는 종료한 소유자 슬롯을 재사용하지 않는다.
        if (nextOwner > rules.MaxPlayers) return JoinFailure.WorldFull;
        int columns = (int)Math.Ceiling(Math.Sqrt(rules.MaxPlayers));
        float spacing = rules.MapSize / (columns + 1);
        if (spacing < settings.MinBaseDistance) throw new InvalidOperationException("현재 맵에서 최소 본진 간격을 유지할 수 없습니다.");
        int slot = nextOwner - 1;
        var basePosition = BasePosition(slot);
        var legionPosition = basePosition + new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartUnitCount + 1));
        int level=1;long cores=rules.StartingCores;
        if(demo)
        {
            while(level<=rules.MaxBaseLevel&&!rules.IsUnlocked(level,"forge_energy"))level++;
            if(level>rules.MaxBaseLevel)throw new InvalidOperationException("데모에 필요한 에너지 양성소 해금이 없습니다.");
            cores=0;for(int index=0;index<rules.Elements.Count;index++)cores=checked(cores+rules.TrainingCost(index,element));
        }
        var spawn = new PlayerSpawn(nextOwner, rules.Elements[element].Id, basePosition, legionPosition,level,cores,demo,demo);
        if (!World.TryAddPlayer(spawn)) return JoinFailure.WorldFull;
        playerId = nextOwner++; connected.Add(playerId);
        return JoinFailure.None;
    }

    public CommandAdmission Move(int playerId, ulong sequence, Vector2 direction) => connected.Contains(playerId)
        ? World.TryEnqueue(playerId, SimCommand.Move(sequence, direction)) : CommandAdmission.UnknownPlayer;
    public CommandAdmission Command(int playerId,SimCommand command)=>connected.Contains(playerId)?World.TryEnqueue(playerId,command):CommandAdmission.UnknownPlayer;

    private Vector2 BasePosition(int slot)
    {
        int columns=(int)Math.Ceiling(Math.Sqrt(rules.MaxPlayers));float spacing=rules.MapSize/(columns+1);
        return new Vector2((slot%columns+1)*spacing,(slot/columns+1)*spacing);
    }

    public void Leave(int playerId)
    {
        if (connected.Remove(playerId)) World.StopPlayer(playerId);
    }

    public void Step()
    {
        if (ConnectedCount > 0 || !settings.PauseWhenEmpty) World.Step();
    }
}
