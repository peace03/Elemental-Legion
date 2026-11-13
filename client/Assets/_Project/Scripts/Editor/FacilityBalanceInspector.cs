using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ElementalLegion.Client.EditModeTests")]

namespace ElementalLegion.Client.Editor
{
    internal sealed class FacilityBalanceInspector : ScriptableObject
    {
        internal const string AssetPath = "Packages/com.elementallegion.shared/Data/gamedata.json";
        private static readonly Regex JsonNumberAtEnd = new Regex(@"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$");
        private static readonly Regex LineBreak = new Regex(@"\r\n|\n|\r");
        private static FacilityBalanceInspector instance;

        [SerializeField] private string sourceHash;
        [SerializeField] private string draftJson;

        internal string SourceHash => sourceHash;
        internal string DraftJson { get => draftJson; set => draftJson = value; }

        [MenuItem("Elemental Legion/밸런스 인스펙터")]
        private static void Open()
        {
            if (instance == null)
            {
                instance = CreateInstance<FacilityBalanceInspector>();
                instance.name = "밸런스 인스펙터";
                instance.hideFlags = HideFlags.DontSave;
            }
            EditorUtility.OpenPropertyEditor(instance);
        }

        internal static string SourcePath()
        {
            PackageInfo package = PackageInfo.FindForAssetPath(AssetPath);
            if (package == null || (package.source != PackageSource.Local && package.source != PackageSource.Embedded))
                throw new InvalidOperationException("편집할 로컬 공유 패키지를 찾을 수 없습니다.");
            return Path.Combine(package.resolvedPath, "Data", "gamedata.json");
        }

        internal void Reload(string path)
        {
            var source = new GameDataDocument(File.ReadAllText(path));
            _ = new GameRules(source);
            sourceHash = source.Sha256;
            draftJson = source.Root.ToString(Formatting.None);
        }

        internal void Save(string path)
        {
            string original = File.ReadAllText(path);
            var source = new GameDataDocument(original);
            if (source.Sha256 != sourceHash)
                throw new InvalidOperationException("다른 곳에서 JSON을 수정했습니다. 원본을 다시 불러온 뒤 편집하세요.");

            JObject draft = JObject.Parse(draftJson);
            draft.SelectToken("population.max").Replace(draft.SelectToken("baseLevels").Last["populationCap"].DeepClone());
            long localSupply = LocalCoreSupply(draft);
            if (localSupply < 0 || localSupply > int.MaxValue)
                throw new ArgumentException("근거리·공용 분당 핵 공급량이 허용 범위를 벗어났습니다.");
            draft.SelectToken("economy.mapCoreSpawnPerPlayerPerMinute").Replace(new JValue((int)localSupply));
            string updated = ApplyNumbers(original, source.Root, draft);
            var document = new GameDataDocument(updated);
            var rules = new GameRules(document);
            ValidateEcology(rules, new NetworkSettings(document).MinBaseDistance);
            if (updated != original)
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, updated, new UTF8Encoding(false));
                    if (File.ReadAllText(path) != original)
                        throw new InvalidOperationException("저장하는 동안 원본 JSON이 변경됐습니다. 다시 불러오세요.");
                    File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
            sourceHash = document.Sha256;
            draftJson = document.Root.ToString(Formatting.None);
        }

        private static void ValidateEcology(GameRules rules, float worldMinBaseDistance)
        {
            ValidateWorld(rules, worldMinBaseDistance, rules.MaxPlayers);
            if (rules.MaxPlayers < 2) return;
            for (int players = 2; players <= rules.MaxPlayers; players++)
                ValidateWorld(rules.ForRoom(players), rules.RoomMinBaseDistance, players);
        }

        private static void ValidateWorld(GameRules rules, float minBaseDistance, int playerCount)
        {
            string worldName = rules.RoomPlayers == 0 ? "상시 월드" : $"{rules.RoomPlayers}인 방";
            try
            {
                var world = new SimulationWorld(rules, new WorldSetup(), seed: 1);
                world.EnableEcology(minBaseDistance);
                string startingElement = rules.Elements[rules.StartingElements[0]].Id;
                for (int player = 1; player <= playerCount; player++)
                    if (!world.TryChooseSpawn(out var basePosition, out var legionPosition) ||
                        !world.TryAddPlayer(new PlayerSpawn(player, startingElement, basePosition, legionPosition)))
                        throw new ArgumentException($"{player}번째 플레이어가 시작할 자리가 없습니다.");
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException($"{worldName}에 중립을 배치할 수 없습니다. {exception.Message}", exception);
            }
        }

        private static string ApplyNumbers(string original, JObject source, JObject draft)
        {
            var edits = new List<(int Start, int Length, string Value)>();
            int[] lineStarts = new[] { 0 }.Concat(LineBreak.Matches(original).Cast<Match>()
                .Select(match => match.Index + match.Length)).ToArray();
            foreach (JValue value in draft.Descendants().OfType<JValue>())
            {
                JToken before = source.SelectToken(value.Path);
                if (JToken.DeepEquals(before, value)) continue;
                if (!IsNumber(value) || before == null || !IsNumber(before) || !(before is IJsonLineInfo location) || !location.HasLineInfo())
                    throw new InvalidOperationException($"{value.Path} 수치 위치를 확인할 수 없습니다.");

                int lineStart = lineStarts[location.LineNumber - 1];
                int lineEnd = lineStart + location.LinePosition;
                Match number = JsonNumberAtEnd.Match(original.Substring(lineStart, lineEnd - lineStart));
                if (!number.Success) throw new InvalidOperationException($"{value.Path} 원본 수치를 찾을 수 없습니다.");
                edits.Add((lineStart + number.Index, number.Length, value.ToString(Formatting.None)));
            }
            var text = new StringBuilder(original);
            foreach (var edit in edits.OrderByDescending(item => item.Start))
            {
                text.Remove(edit.Start, edit.Length);
                text.Insert(edit.Start, edit.Value);
            }
            string updated = text.ToString();
            if (!JToken.DeepEquals(JObject.Parse(updated), draft))
                throw new InvalidOperationException("편집한 수치와 저장할 JSON이 일치하지 않습니다.");
            return updated;
        }

        private static bool IsNumber(JToken token) => token.Type == JTokenType.Integer || token.Type == JTokenType.Float;

        internal static long LocalCoreSupply(JObject draft)
        {
            JToken opening = draft.SelectToken("economy.opening");
            int nearby = (int)opening["nearbyBundlesPerMinute"];
            int shared = (int)opening["sharedBundlesPerMinute"];
            int amount = (int)opening["bundleSize"];
            return nearby < 0 || shared < 0 || amount < 1 ? -1 : ((long)nearby + shared) * amount;
        }
    }

    [CustomEditor(typeof(FacilityBalanceInspector))]
    internal sealed class FacilityBalanceEditor : UnityEditor.Editor
    {
        private static readonly (string Key, string Label, string BranchA, string BranchB)[] Facilities =
        {
            ("collector", "포집기", "생성 주기 (초)", "생성량 (핵/회)"),
            ("tower", "수호 위성", "공격 거리 (m)", "공격 배율"),
            ("forge_nature", "응집로", "양성 비용 할인 (%)", "양성 공격력 증가 (%)"),
            ("forge_energy", "점화로", "양성 비용 할인 (%)", "양성 공격력 증가 (%)")
        };

        private string status;
        private MessageType statusType;
        private bool showPlacement = true;
        private bool showMapCores = true;
        private bool showBase;
        private bool showConstruction;
        private readonly bool[] showFacility = new bool[Facilities.Length];

        public override void OnInspectorGUI()
        {
            var model = (FacilityBalanceInspector)target;
            EditorGUILayout.HelpBox("편집한 숫자는 Enter로 확정하세요. JSON에 저장한 수치는 같은 데이터로 서버와 클라이언트를 다시 실행하고 새 방을 만들면 적용됩니다. 기존 저장을 열 때는 저장 당시 JSON이 필요합니다.", MessageType.Info);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusType);
            if (string.IsNullOrEmpty(model.DraftJson) && status == null)
                Run(() => model.Reload(FacilityBalanceInspector.SourcePath()));
            if (string.IsNullOrEmpty(model.DraftJson))
            {
                if (GUILayout.Button("다시 불러오기")) Reload(model);
                return;
            }

            JObject draft = JObject.Parse(model.DraftJson);
            EditorGUI.BeginChangeCheck();
            DrawPlacement(draft);
            DrawMapCores(draft);
            DrawConstruction(draft);
            DrawBaseLevels(draft);
            for (int index = 0; index < Facilities.Length; index++) DrawFacility(draft, index);
            if (EditorGUI.EndChangeCheck())
            {
                string edited = draft.ToString(Formatting.None);
                if (edited != model.DraftJson)
                {
                    Undo.RecordObject(model, "시설 밸런스 편집");
                    model.DraftJson = edited;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("JSON에 저장"))
                    Run(() =>
                    {
                        model.Save(FacilityBalanceInspector.SourcePath());
                        AssetDatabase.ImportAsset(FacilityBalanceInspector.AssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                        status = $"저장 완료 · SHA-256 {model.SourceHash}";
                        statusType = MessageType.Info;
                    });
                if (GUILayout.Button("다시 불러오기")) Reload(model);
            }
        }

        private void Reload(FacilityBalanceInspector model) => Run(() =>
        {
            Undo.RecordObject(model, "밸런스 원본 불러오기");
            model.Reload(FacilityBalanceInspector.SourcePath());
        });

        private void Run(Action operation)
        {
            try
            {
                status = null;
                operation();
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException ||
                exception is IOException || exception is UnauthorizedAccessException || exception is JsonException)
            {
                status = exception.Message;
                statusType = MessageType.Error;
            }
        }

        private void DrawPlacement(JObject draft)
        {
            showPlacement = EditorGUILayout.Foldout(showPlacement, "중립 무리 배치", true);
            if (!showPlacement) return;
            using (new EditorGUI.IndentLevelScope())
            {
                double seconds = Double(draft, "neutral.walkSpacingSeconds", "목표 도보 시간 (초)");
                Double(draft, "neutral.respawnSeconds", "처치 후 재생성 (초)");
                Double(draft, "world.roomMapSizeAtTwoPlayers", "2인 방 한 변 (m)");
                Double(draft, "world.mapSize", "최대 맵 한 변 (m)");
                double distance = seconds * (double)draft.SelectToken("world.legionSpeed");
                EditorGUILayout.LabelField("환산 거리 (m)", distance.ToString("0.##", CultureInfo.CurrentCulture));
            }
        }

        private void DrawMapCores(JObject draft)
        {
            showMapCores = EditorGUILayout.Foldout(showMapCores, "맵 원소핵 출현", true);
            if (!showMapCores) return;
            using (new EditorGUI.IndentLevelScope())
            {
                const string opening = "economy.opening.";
                int amount = Integer(draft, opening + "bundleSize", "묶음당 핵 수");
                Integer(draft, opening + "nearbyBundlesPerMinute", "시작점 근처 (묶음/분/인)");
                Integer(draft, opening + "sharedBundlesPerMinute", "본진 사이 (묶음/분/인)");
                int exploration = Integer(draft, opening + "explorationBundlesPerMinute", "탐색 구역 (묶음/분/인)");
                Double(draft, opening + "explorationCoreLifetimeSeconds", "탐색 핵 유지 시간 (초)");
                long localSupply = FacilityBalanceInspector.LocalCoreSupply(draft);
                long explorationSupply = (long)exploration * amount;
                if (localSupply >= 0 && exploration >= 0)
                {
                    EditorGUILayout.LabelField("분당 공급 (1인)", $"기본 {localSupply:N0}핵 + 탐색 {explorationSupply:N0}핵");
                    EditorGUILayout.HelpBox("각 구역의 빈도를 0으로 설정하면 해당 구역의 주기 생성이 중단됩니다.", MessageType.None);
                }
            }
        }

        private void DrawConstruction(JObject draft)
        {
            showConstruction = EditorGUILayout.Foldout(showConstruction, "건설과 기본 양성 비용", true);
            if (!showConstruction) return;
            using (new EditorGUI.IndentLevelScope())
            {
                Integer(draft, "buildings.collector.cost", "포집기 (핵)");
                Integer(draft, "towers.cost", "수호 위성 (핵)");
                Integer(draft, "buildings.forge_nature.cost", "응집로 (핵)");
                Integer(draft, "buildings.forge_energy.cost", "점화로 (핵)");
                Integer(draft, "buildings.dome.cost", "자기권 (핵)");
                Integer(draft, "economy.natureUnitCost", "자연 원소 (핵/기)");
                Integer(draft, "economy.energyUnitCost", "에너지 원소 (핵/기)");
                Double(draft, "economy.mainElementDiscount", "주력 할인 비율 (0~1)");
            }
        }

        private void DrawBaseLevels(JObject draft)
        {
            showBase = EditorGUILayout.Foldout(showBase, "원시핵 레벨", true);
            if (!showBase) return;
            using (new EditorGUI.IndentLevelScope())
            {
                int levelCount = ((JArray)draft["baseLevels"]).Count;
                for (int index = 0; index < levelCount; index++)
                {
                    string path = $"baseLevels[{index}]";
                    string requirement = index >= 1 && index <= GameRules.MaxFacilityUpgradeRank ? $" · 시설 {index}단계 해금" : string.Empty;
                    EditorGUILayout.LabelField($"Lv{index + 1}{requirement}", EditorStyles.boldLabel);
                    if (index > 0) Integer(draft, path + ".upgradeCost", "업그레이드 비용 (핵)");
                    Integer(draft, path + ".populationCap", "총 인구");
                    Integer(draft, path + ".garrisonCap", "수비대");
                    Integer(draft, path + ".towerSlots", "수호 위성 슬롯");
                }
            }
        }

        private void DrawFacility(JObject draft, int index)
        {
            var facility = Facilities[index];
            showFacility[index] = EditorGUILayout.Foldout(showFacility[index], facility.Label + " 업그레이드", true);
            if (!showFacility[index]) return;
            using (new EditorGUI.IndentLevelScope())
            {
                if (facility.Key == "collector")
                {
                    Double(draft, "buildings.collector.production.intervalSeconds", "기본 생성 주기 (초)");
                    Integer(draft, "buildings.collector.production.coresPerCycle", "기본 생성량 (핵/회)");
                }
                else if (facility.Key == "tower")
                {
                    Double(draft, "towers.range", "기본 공격 거리 (m)");
                    Double(draft, "towers.dpsInUnitsOfSameElement", "기본 공격 배율");
                }
                DrawBranch(draft, facility.Key, "a", facility.BranchA);
                DrawBranch(draft, facility.Key, "b", facility.BranchB);
            }
        }

        private static void DrawBranch(JObject draft, string facility, string branch, string label)
        {
            string branchPath = $"facilityUpgrades.{facility}.{branch}";
            for (int rank = 1; rank <= GameRules.MaxFacilityUpgradeRank; rank++)
            {
                string path = $"{branchPath}[{rank - 1}]";
                EditorGUILayout.LabelField($"{label} · {rank}단계 · 원시핵 Lv{GameRules.FacilityUpgradeRequiredLevel(rank)}", EditorStyles.boldLabel);
                Integer(draft, path + ".cost", "비용 (핵)");
                bool integerEffect = (facility == "collector" && branch == "b") || facility.StartsWith("forge_", StringComparison.Ordinal);
                double value = integerEffect ? Integer(draft, path + ".effect", label) : Double(draft, path + ".effect", label);
                if (facility == "collector") DrawProductionPreview(draft, branch, value);
                if (facility.StartsWith("forge_", StringComparison.Ordinal) && branch == "a") DrawTrainingPreview(draft, facility, value);
            }
        }

        private static void DrawProductionPreview(JObject draft, string branch, double value)
        {
            double seconds = branch == "a" ? value : (double)draft.SelectToken("buildings.collector.production.intervalSeconds");
            double amount = branch == "b" ? value : (int)draft.SelectToken("buildings.collector.production.coresPerCycle");
            int tickRate = (int)draft.SelectToken("world.tickRate");
            double ticks = Math.Ceiling(seconds * tickRate);
            if (ticks > 0 && amount > 0)
                EditorGUILayout.LabelField("분당 생산량", (60 * tickRate * amount / ticks).ToString("0.##", CultureInfo.CurrentCulture) + "핵");
        }

        private static void DrawTrainingPreview(JObject draft, string facility, double discount)
        {
            if (double.IsNaN(discount) || discount < 0 || discount >= 100) return;
            bool nature = facility == "forge_nature";
            int basic = (int)draft.SelectToken(nature ? "economy.natureUnitCost" : "economy.energyUnitCost");
            decimal multiplier = 1 - (decimal)discount / 100;
            int perUnit = Math.Max(1, (int)Math.Ceiling(basic * multiplier));
            string preview = $"{perUnit}핵";
            if (nature)
            {
                double mainDiscount = (double)draft.SelectToken("economy.mainElementDiscount");
                if (double.IsNaN(mainDiscount) || mainDiscount < 0 || mainDiscount > 1) return;
                decimal mainCost = Math.Truncate(basic * (1 - (decimal)mainDiscount));
                preview += $" / 주력 {Math.Max(1, (int)Math.Ceiling(mainCost * multiplier))}핵";
            }
            EditorGUILayout.LabelField("양성 비용 (기당)", preview);
        }

        private static int Integer(JObject draft, string path, string label)
        {
            JToken token = draft.SelectToken(path);
            int current = (int)token;
            int edited = EditorGUILayout.DelayedIntField(label, current);
            if (edited != current) token.Replace(new JValue(edited));
            return edited;
        }

        private static double Double(JObject draft, string path, string label)
        {
            JToken token = draft.SelectToken(path);
            double current = (double)token;
            double edited = EditorGUILayout.DelayedDoubleField(label, current);
            if (edited != current) token.Replace(new JValue(edited));
            return edited;
        }
    }
}
