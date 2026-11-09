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
    private readonly Predicate<int> ownerRemoved;
    private long nextOwner = 1;
    private readonly int[]? m3Owners;
    private readonly bool m4Demo;
    private readonly List<(int Owner, BotBrain Brain)> ai = new();
    private readonly bool aiMatch;
    internal bool HasWinner => (aiMatch || ai.Count > 0) && World.PlayerCount == 1;
    internal int LivingAiCount => ai.Count(bot => World.TryGetView(bot.Owner, out _));
    public SimulationWorld World { get; }
    public GameRules Rules => rules;
    public int ConnectedCount => connected.Count;
    public IReadOnlyCollection<int> ConnectedOwners => connected;
    public int NextPlayerId => checked((int)nextOwner);

    public NetworkWorld(GameRules rules, NetworkSettings settings, string scenario = "normal")
        : this(rules, settings, scenario, waitingRoom: false) { }

    internal NetworkWorld(GameRules rules, NetworkSettings settings, string scenario, bool waitingRoom)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (scenario is not ("normal" or "m3-demo" or "m4-demo" or "m5-demo"))
            throw new ArgumentException("알 수 없는 서버 시나리오입니다.", nameof(scenario));
        int towerCapacity = 0;
        for (int level = 1; level <= rules.MaxBaseLevel; level++)
            towerCapacity = Math.Max(towerCapacity, rules.TowerSlots(level));
        // 본진, 포집기, 두 양성소와 자기권은 소유자마다 하나씩 지을 수 있다.
        if ((5L + towerCapacity) * rules.MaxPlayers > GameplayCodec.MaxBuildings)
            throw new ArgumentException("시설 상한이 관측 프레임의 건물 한도를 넘습니다.", nameof(rules));
        m4Demo = scenario is "m4-demo" or "m5-demo";
        var setup = new WorldSetup { CameraVisibility = true };
        if (scenario == "m3-demo")
        {
            m3Owners = new int[rules.MaxPlayers];
            for (int slot = 0; slot < m3Owners.Length; slot++)
            {
                Vector2 origin = BasePosition(slot);
                var first = origin + new Vector2(0, rules.VisionRadius * 2);
                var second = origin + new Vector2(rules.VisionRadius / 2, rules.VisionRadius * 2.5f);
                if (first.X > rules.MapSize || first.Y > rules.MapSize || second.X > rules.MapSize || second.Y > rules.MapSize)
                    throw new ArgumentException("데모 중립 배치가 맵 밖입니다.");
                setup.Units.Add(new UnitSpawn(-1, "fire", first));
                setup.Units.Add(new UnitSpawn(-1, "fire", second, converted: true));
            }
        }
        World = new SimulationWorld(rules, setup, 1);
        if (m3Owners == null && !waitingRoom)
            World.EnableEcology(rules.RoomPlayers == 0 ? settings.MinBaseDistance : rules.RoomMinBaseDistance);
        ownerRemoved = owner => !World.TryGetView(owner, out _);
    }

    public NetworkWorld(GameRules rules, NetworkSettings settings, WorldCheckpoint checkpoint, int nextPlayerId,
        string scenario = "normal", IReadOnlyList<int>? aiOwners = null, IReadOnlyList<AiDifficulty>? aiDifficulties = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = rules.ForRoom(checkpoint.RoomPlayers);
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (nextPlayerId < 1) throw new ArgumentOutOfRangeException(nameof(nextPlayerId));
        if (aiDifficulties != null && aiOwners == null)
            throw new ArgumentException("AI 소유자 없이 난이도만 복원할 수 없습니다.", nameof(aiDifficulties));
        ValidateAiDifficulties(aiOwners?.Count ?? 0, aiDifficulties);
        World = SimulationWorld.RestoreCheckpoint(this.rules, checkpoint, cameraVisibility: true);
        nextOwner = nextPlayerId;
        m4Demo = scenario == "m5-demo";
        ownerRemoved = owner => !World.TryGetView(owner, out _);
        if (aiOwners != null)
            for (int index = 0; index < aiOwners.Count; index++)
            {
                int owner = aiOwners[index];
                World.TryResumeGhost(owner);
                ai.Add((owner, new BotBrain(this.rules, owner, playMatch: true, difficulty: aiDifficulties?[index] ?? AiDifficulty.Normal)));
            }
    }

    public bool TryResume(int owner)
    {
        if (connected.Contains(owner) || !World.TryResumeGhost(owner)) return false;
        connected.Add(owner);
        return true;
    }

    public JoinFailure TryJoin(int element, out int playerId, string nickname = PlayerNickname.Default, int colorId = -1)
    {
        playerId = -1;
        if (!rules.StartingElements.Contains(element)) return JoinFailure.InvalidElement;
        if (!PlayerNickname.IsValid(nickname)) return JoinFailure.InvalidNickname;
        if (nextOwner > int.MaxValue) return JoinFailure.WorldFull;
        Vector2 basePosition, legionPosition;
        int level = 1;
        long cores = rules.StartingCores;
        int demoSlot = -1;
        bool m3Demo = m3Owners != null;
        if (m3Owners != null)
        {
            if (!TryChooseDemoSpawn(out demoSlot, out basePosition, out legionPosition)) return JoinFailure.WorldFull;
            while (level <= rules.MaxBaseLevel && !rules.IsUnlocked(level, "forge_energy")) level++;
            if (level > rules.MaxBaseLevel) throw new InvalidOperationException("데모에 필요한 에너지 양성소 해금이 없습니다.");
            cores = 0;
            for (int index = 0; index < rules.Elements.Count; index++)
                cores = checked(cores + rules.TrainingCost(index, element));
        }
        else
        {
            if (!World.TryChooseSpawn(out basePosition, out legionPosition)) return JoinFailure.WorldFull;
            if (m4Demo) cores = ConstructionBudget(element);
        }
        var spawn = new PlayerSpawn((int)nextOwner, rules.Elements[element].Id, basePosition, legionPosition, level, cores,
            m3Demo, m3Demo, nickname, colorId);
        if (!World.TryAddPlayer(spawn)) return JoinFailure.WorldFull;
        playerId = (int)nextOwner++;
        if (m3Owners != null) m3Owners[demoSlot] = playerId;
        connected.Add(playerId);
        return JoinFailure.None;
    }

    private bool TryChooseDemoSpawn(out int slot, out Vector2 basePosition, out Vector2 legionPosition)
    {
        var offset = new Vector2(0, rules.BaseRadius + rules.SlotSpacing * (rules.StartUnitCount + 1));
        for (slot = 0; slot < m3Owners!.Length; slot++)
        {
            if (World.TryGetView(m3Owners[slot], out _)) continue;
            basePosition = BasePosition(slot);
            legionPosition = basePosition + offset;
            if (World.IsSafePlayerSpawn(basePosition, legionPosition, settings.MinBaseDistance)) return true;
        }
        basePosition = legionPosition = default;
        return false;
    }

    private long ConstructionBudget(int mainElement)
    {
        long budget = 0;
        for (int level = 2; level <= rules.MaxBaseLevel; level++)
            budget += rules.UpgradeCost(level);
        budget += (long)rules.BuildingCost(BuildingKind.Collector) + rules.BuildingCost(BuildingKind.NatureForge) +
            rules.BuildingCost(BuildingKind.EnergyForge) + rules.BuildingCost(BuildingKind.Dome);
        budget += (long)rules.TowerSlots(rules.MaxBaseLevel) *
            Math.Max(rules.BuildingCost(BuildingKind.NatureTower), rules.BuildingCost(BuildingKind.EnergyTower));
        for (int index = 0; index < rules.Elements.Count; index++)
            budget += rules.TrainingCost(index, mainElement);
        return budget;
    }

    public CommandAdmission Move(int playerId, ulong sequence, Vector2 direction) => connected.Contains(playerId)
        ? World.TryEnqueue(playerId, SimCommand.Move(sequence, direction)) : CommandAdmission.UnknownPlayer;
    public CommandAdmission Command(int playerId,SimCommand command)=>connected.Contains(playerId)?World.TryEnqueue(playerId,command):CommandAdmission.UnknownPlayer;

    internal JoinFailure AddAiOpponents(int count, int humanElement, IReadOnlyList<AiDifficulty>? difficulties = null, IReadOnlyList<int>? colorIds = null)
    {
        ValidateAiDifficulties(count, difficulties);
        if (colorIds != null && (colorIds.Count != count || colorIds.Any(color => !PlayerPalette.IsValid(color)) ||
            colorIds.Distinct().Count() != count || colorIds.Any(color => !World.IsColorAvailable(color))))
            throw new ArgumentException("AI 소유 색이 잘못됐습니다.", nameof(colorIds));
        int start = rules.StartingElements.IndexOf(humanElement);
        for (int index = 0; index < count; index++)
        {
            int element = rules.StartingElements[(start + 1 + index) % rules.StartingElements.Count];
            JoinFailure failure = TryJoin(element, out int owner, $"AI 군단 {ai.Count + 1}", colorIds?[index] ?? -1);
            if (failure != JoinFailure.None) return failure;
            connected.Remove(owner);
            ai.Add((owner, new BotBrain(rules, owner, playMatch: true, difficulty: difficulties?[index] ?? AiDifficulty.Normal)));
        }
        return JoinFailure.None;
    }

    internal int[]? CaptureAiOwners() => ai.Count == 0 ? null : ai.Select(bot => bot.Owner).ToArray();
    internal AiDifficulty[]? CaptureAiDifficulties() => ai.Count == 0 ? null : ai.Select(bot => bot.Brain.Difficulty).ToArray();

    private static void ValidateAiDifficulties(int count, IReadOnlyList<AiDifficulty>? difficulties)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (difficulties == null) return;
        if (difficulties.Count != count)
            throw new ArgumentException("AI 수와 난이도 수가 다릅니다.", nameof(difficulties));
        for (int index = 0; index < difficulties.Count; index++)
            if (difficulties[index] > AiDifficulty.Hard)
                throw new ArgumentOutOfRangeException(nameof(difficulties), "알 수 없는 AI 난이도입니다.");
    }

    internal BotBrainState[] CaptureAiMigration() => ai.Where(bot => World.TryGetView(bot.Owner, out _))
        .Select(bot => bot.Brain.CaptureState()).ToArray();

    /// <summary>접속 중인 사람만 복귀시키고 다른 소유자는 기존 로그아웃 규칙을 적용한다.</summary>
    internal NetworkWorld(GameRules rules, NetworkSettings settings, MigrationWorldState snapshot, int nextPlayerId,
        IReadOnlyList<BotBrainState> brains, IReadOnlyCollection<int> activeHumans, IReadOnlyCollection<int> humanOwners,
        bool aiMatch = false)
    {
        this.aiMatch = aiMatch;
        this.rules = rules.ForRoom(snapshot.RoomPlayers);
        this.settings = settings;
        World = SimulationWorld.RestoreMigrationState(this.rules, snapshot, cameraVisibility: true);
        nextOwner = nextPlayerId;
        ownerRemoved = owner => !World.TryGetView(owner, out _);
        foreach (BotBrainState state in brains)
            ai.Add((state.PlayerId, BotBrain.RestoreState(this.rules, state)));
        foreach (int owner in humanOwners)
        {
            if (!World.TryGetView(owner, out _)) continue;
            if (activeHumans.Contains(owner))
            {
                // 이미 활동 중인 소유자는 이 호출이 false를 반환하며 입력 순번을 유지한다.
                World.TryResumeGhost(owner);
                connected.Add(owner);
            }
            else World.BeginGhost(owner);
        }
    }

    internal bool HasMigrationConnection(int owner) => connected.Contains(owner) && World.TryGetView(owner, out _);

    private Vector2 BasePosition(int slot)
    {
        int columns = (int)Math.Ceiling(Math.Sqrt(rules.MaxPlayers));
        float spacing = rules.MapSize / (columns + 1);
        if (spacing < settings.MinBaseDistance)
            throw new ArgumentException("현재 맵에서 최소 본진 간격을 유지할 수 없습니다.");
        return new Vector2((slot % columns + 1) * spacing, (slot / columns + 1) * spacing);
    }

    public void Leave(int playerId)
    {
        if (connected.Remove(playerId)) World.BeginGhost(playerId);
    }

    public void Step()
    {
        if (ConnectedCount == 0 && !World.HasGhosts && !World.HasConversionCandidates && settings.PauseWhenEmpty) return;
        if (ConnectedCount > 0)
            foreach (var bot in ai)
                if (World.TryGetView(bot.Owner, out WorldView view) && bot.Brain.Decide(view) is SimCommand command)
                    World.TryEnqueue(bot.Owner, command);
        World.Step();
        connected.RemoveWhere(ownerRemoved);
    }
}
