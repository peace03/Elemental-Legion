using System;
using ElementalLegion.Shared.Data;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Shared.Net
{
    /// <summary>JSON의 통신 설정을 시작 시 한 번 검증하고 고정 틱의 전송 간격으로 변환한다.</summary>
    public sealed class NetworkSettings
    {
        public int Port { get; }
        public int LegionEveryTicks { get; }
        public int UnitsEveryTicks { get; }
        public double InterpolationSeconds { get; }
        public float MinBaseDistance { get; }
        public bool PauseWhenEmpty { get; }

        public NetworkSettings(GameDataDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            JObject root = document.Root;
            int tickRate = Integer(root, "world.tickRate", 1, 1000);
            Port = Integer(root, "server.port", 1, ushort.MaxValue);
            int legionHz = Integer(root, "server.snapshot.legionHz", 1, tickRate);
            int unitHz = Integer(root, "server.snapshot.unitHz", 1, tickRate);
            if (tickRate % legionHz != 0 || tickRate % unitHz != 0) throw new ArgumentException("스냅샷 주기는 tickRate의 약수여야 합니다.");
            LegionEveryTicks = tickRate / legionHz;
            UnitsEveryTicks = tickRate / unitHz;
            InterpolationSeconds = Integer(root, "server.interpolationDelayMs", 0, 10000) / 1000.0;
            JToken distance = root.SelectToken("spawn.minBaseDistance");
            if (distance == null || distance.Type != JTokenType.Float && distance.Type != JTokenType.Integer) throw new ArgumentException("spawn.minBaseDistance가 필요합니다.");
            MinBaseDistance = distance.Value<float>();
            if (!Protocol.Finite(MinBaseDistance) || MinBaseDistance <= 0) throw new ArgumentException("본진 간격은 유한한 양수여야 합니다.");
            JToken pause = root.SelectToken("server.pauseWhenEmpty");
            if (pause == null || pause.Type != JTokenType.Boolean) throw new ArgumentException("server.pauseWhenEmpty는 bool이어야 합니다.");
            PauseWhenEmpty = pause.Value<bool>();
        }

        private static int Integer(JObject root, string path, int min, int max)
        {
            JToken token = root.SelectToken(path);
            if (token == null || token.Type != JTokenType.Integer || token.Value<long>() < min || token.Value<long>() > max) throw new ArgumentException(path + " 범위가 잘못됐습니다.");
            return token.Value<int>();
        }
    }
}
