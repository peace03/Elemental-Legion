using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    public sealed partial class CombatHud
    {
        private RectTransform hudHeader, navigation, mapPanel, commands, roster, hudRect;
        private RectTransform[] rosterIconRects, trainingIconRects;
        private TMP_Text[] rosterLabels;
        private string[] rosterFormats;
        private RectTransform trainingProgress;
        private Vector2 layoutSize;

        /// <summary>수신 상태와 기존 버튼을 유지하면서 시안의 가장자리 HUD로 배치한다.</summary>
        internal void BuildObservatory(ObservatoryUi ui, GameRules gameRules, ElementalVisuals buildingVisuals)
        {
            if (Map != null) return;
            skin = ui;
            visuals = buildingVisuals;
            worldCamera = visuals.TargetCamera;
            scaler = GetComponentInParent<CanvasScaler>();
            hudRect = (RectTransform)content.transform;
            interaction = content.AddComponent<CanvasGroup>();
            foreach (TMP_Text label in GetComponentsInChildren<TMP_Text>(true)) label.font = ui.Font;
            Transform oldAudio = content.transform.Find("AudioControls");
            if (oldAudio != null) oldAudio.gameObject.SetActive(false);

            hudHeader = (RectTransform)economy.transform.parent;
            hudHeader.GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Surface;
            hudHeader.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            ui.Icon(hudHeader, "Core emblem", ui.Art.Building(ElementalLegion.Shared.Sim.BuildingKind.Base), 8, 7, 40, 40);
            ui.Label(hudHeader, "Brand", "ELEMENTAL LEGION", 17, 51, 7, 197, 40, ObservatoryUi.Gold);
            economy.fontSize = 15;
            composition.transform.SetParent(content.transform, false);
            navigation = ObservatoryUi.Rect(hudHeader, "Navigation", 0, 0, 208, 52);
            ui.Button(navigation, "현황", 0, 8, 96, 36, () => DashboardRequested?.Invoke());
            menuKey = ui.Button(navigation, "메뉴", 104, 8, 104, 36, () => MenuRequested?.Invoke()).GetComponentInChildren<TMP_Text>();
            menuKey.fontSize = 13;

            mapPanel = ObservatoryUi.Panel(content.transform, "Observatory map", 0, 0, 238, 286, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(mapPanel, Vector2.zero, new Vector2(16, 16), mapPanel.sizeDelta);
            ui.Label(mapPanel, "Map title", "관측소", 15, 12, 5, 76, 25, ObservatoryUi.Gold);
            ui.Label(mapPanel, "Map north", "북 ↑", 12, 89, 5, 46, 25, ObservatoryUi.Muted);
            ui.Button(mapPanel, "군단 중심", 140, 5, 86, 25, () => CameraCenterRequested?.Invoke())
                .GetComponentInChildren<TMP_Text>().fontSize = 12;
            RectTransform terrain = ObservatoryUi.Rect(mapPanel, "Exploration", 12, 34, 214, 214);
            RawImage mapImage = terrain.gameObject.AddComponent<RawImage>();
            mapImage.raycastTarget = false;
            terrain.gameObject.AddComponent<RectMask2D>();
            RectTransform markers = ObservatoryUi.Rect(terrain, "Observed markers", 0, 0, 214, 214);
            ObservatoryUi.Fill(markers);
            Map = markers.gameObject.AddComponent<ObservatoryMap>();
            Map.Initialize(mapImage, gameRules);
            Map.FocusRequested += position => MapFocusRequested?.Invoke(position);
            mapStatus = ui.Label(mapPanel, "Map status", "", 13, 12, 251, 214, 28, ObservatoryUi.Accent);

            commands = ObservatoryUi.Panel(content.transform, "Observatory commands", 0, 0, 258, 194, ObservatoryUi.Surface, true);
            ObservatoryUi.Place(commands, Vector2.right, new Vector2(-16, 16), commands.sizeDelta);
            ui.Label(commands, "Command title", "군단 명령", 15, 12, 4, 234, 24, ObservatoryUi.Gold);
            construction = new ConstructionPanel(ui, content.transform, gameRules, names,
                (kind, x, z, element) => BuildRequested?.Invoke(kind, x, z, element), () => UpgradeRequested?.Invoke());
            ui.Button(commands, "양성", 12, 32, 113, 32, () => trainingPanel.gameObject.SetActive(!trainingPanel.gameObject.activeSelf));
            ui.Button(commands, "건설", 133, 32, 113, 32, () =>
            {
                construction.SetOpen(!construction.IsOpen);
                roster.gameObject.SetActive(!construction.IsOpen);
            });

            trainingPanel = (RectTransform)train[0].transform.parent;
            trainingPanel.GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Surface;
            trainingPanel.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            ui.Label(trainingPanel, "Training title", "원소 양성", 18, 12, 5, 170, 30, ObservatoryUi.Gold);
            ui.Button(trainingPanel, "접기", 208, 6, 66, 27, () => trainingPanel.gameObject.SetActive(false)).GetComponentInChildren<TMP_Text>().fontSize = 12;
            RectTransform progressTrack = ObservatoryUi.Panel(trainingPanel, "Training progress track", 12, 36, 262, 3, ObservatoryUi.Raised);
            trainingProgress = ObservatoryUi.Panel(progressTrack, "Training progress", 0, 0, 0, 0, ObservatoryUi.Accent);
            ObservatoryUi.Fill(trainingProgress);
            trainingIconRects = new RectTransform[train.Length];
            for (int index = 0; index < train.Length; index++)
            {
                ui.StyleButton(train[index]);
                trainingIconRects[index] = ui.Icon(train[index].transform, "Element icon", ui.Art.Element(gameRules.Elements[index].Id), 4, 4, 30, 30).rectTransform;
                labels[index].alignment = TextAlignmentOptions.TopLeft;
                labels[index].lineSpacing = -44;
            }
            composition.transform.SetParent(trainingPanel, false);
            composition.fontSize = 11;
            GameObject queueRoot = RuntimeUiControls.CreateScrollView();
            queueRoot.transform.SetParent(trainingPanel, false);
            queueRoot.GetComponent<UnityEngine.UI.Image>().color = ObservatoryUi.Background;
            trainingQueueScroll = queueRoot.GetComponent<ScrollRect>();
            trainingQueueScroll.horizontalScrollbar.gameObject.SetActive(false);
            trainingQueueScroll.horizontalScrollbar = null;
            trainingQueueScroll.horizontal = false;
            trainingQueueScroll.movementType = ScrollRect.MovementType.Clamped;
            ObservatoryUi.StyleScroll(trainingQueueScroll);
            trainingQueueScroll.content.sizeDelta = new Vector2(0, 60);
            trainingQueue = ui.Label(trainingQueueScroll.content, "Training queue", "", 12, 6, 0, 228, 20, ObservatoryUi.Muted);

            rallyTarget = ui.Dropdown(commands, new[] { "1번에 집결", "2번에 집결", "3번에 집결" }, 0, 12, 72, 142);
            rallyCount = ui.Input(commands, "1", 166, 72, 80);
            rallyCount.contentType = TMP_InputField.ContentType.IntegerNumber;
            rallyCount.characterLimit = 5;
            ((TMP_Text)rallyCount.placeholder).text = "수량";
            rallyTarget.onValueChanged.AddListener(_ => RefreshRallyInput());
            rallyCount.onValueChanged.AddListener(_ => RefreshRallyInput());
            rally.transform.SetParent(commands, false);
            ObservatoryUi.Place((RectTransform)rally.transform, Vector2.up, new Vector2(12, -119), new Vector2(234, 40));
            ui.StyleButton(rally, true);
            rallyKey = rally.GetComponentInChildren<TMP_Text>();
            rallyKey.fontSize = 18;
            rallyKey.alignment = TextAlignmentOptions.Center;
            ObservatoryUi.Fill(rallyKey.rectTransform);
            rallyStatus = ui.Label(commands, "Rally status", "", 13, 12, 163, 234, 25, ObservatoryUi.Accent);
            feedback.transform.SetParent(content.transform, false);
            ObservatoryUi.Place(feedback.rectTransform, new Vector2(0.5f, 1), new Vector2(0, -76), new Vector2(500, 30));
            feedback.fontSize = 15;
            feedback.alignment = TextAlignmentOptions.Center;
            feedback.color = ObservatoryUi.Gold;
            defenseAlert = ui.Label(content.transform, "Defense alert", "", 18, 0, 0, 620, 34, ObservatoryUi.Gold);
            ObservatoryUi.Place(defenseAlert.rectTransform, new Vector2(.5f, 1), new Vector2(0, -110), new Vector2(620, 34));
            defenseAlert.alignment = TextAlignmentOptions.Center;
            defenseAlert.gameObject.SetActive(false);

            roster = ObservatoryUi.Panel(content.transform, "Element composition", 0, 0, 692, 118, ObservatoryUi.Surface, true);
            ui.Label(roster, "Composition title", "원소 구성", 14, 12, 3, 300, 22, ObservatoryUi.Gold);
            rosterLabels = new TMP_Text[train.Length];
            rosterFormats = new string[train.Length];
            rosterIconRects = new RectTransform[train.Length];
            for (int index = 0; index < train.Length; index++)
            {
                rosterIconRects[index] = ui.Icon(roster, "Composition icon", ui.Art.Element(gameRules.Elements[index].Id), 0, 0, 28, 28).rectTransform;
                rosterLabels[index] = ui.Label(roster, "Composition count", "", 10, 0, 0, 90, 16);
                rosterLabels[index].alignment = TextAlignmentOptions.Center;
                rosterFormats[index] = names[index] + " {0}";
            }
            BuildSquadControls(ui);
            trainingPanel.gameObject.SetActive(true);
            buildingTimerLayer = ObservatoryUi.Rect(content.transform, "Building timers", 0, 0, 0, 0);
            ObservatoryUi.Fill(buildingTimerLayer);
            buildingTimerLayer.SetAsFirstSibling();
            RefreshLayout();
        }

        private void RefreshLayout()
        {
            if (hudRect == null || scaler == null) return;
            if (roster.gameObject.activeSelf == construction.IsOpen) roster.gameObject.SetActive(!construction.IsOpen);
            Vector2 size = new Vector2(Mathf.Max(scaler.referenceResolution.x, hudRect.rect.width),
                Mathf.Max(scaler.referenceResolution.y, hudRect.rect.height));
            if (size == layoutSize) return;
            layoutSize = size;
            ObservatoryUi.Place(hudHeader, Vector2.up, new Vector2(16, -12), new Vector2(size.x - 32, 54));
            ObservatoryUi.Place(economy.rectTransform, Vector2.up, new Vector2(250, -8), new Vector2(size.x - 510, 38));
            ObservatoryUi.Place(navigation, Vector2.one, new Vector2(-10, 0), new Vector2(208, 52));
            float panelHeight = size.y - 256;
            ObservatoryUi.Place(trainingPanel, Vector2.one, new Vector2(-16, -76), new Vector2(286, panelHeight));
            float rowHeight = Mathf.Min(44, (panelHeight - 130) / 7);
            for (int index = 0; index < train.Length; index++)
            {
                ObservatoryUi.Place((RectTransform)train[index].transform, Vector2.up,
                    new Vector2(12 + index % 2 * 135, -44 - index / 2 * rowHeight), new Vector2(127, rowHeight - 4));
                ObservatoryUi.Place(trainingIconRects[index], Vector2.up, new Vector2(4, -(rowHeight - 34) * 0.5f), new Vector2(30, 30));
                ObservatoryUi.Place(labels[index].rectTransform, Vector2.up, new Vector2(39, -1), new Vector2(84, rowHeight - 6));
                labels[index].fontSize = rowHeight < 41 ? 10 : 11;
            }
            float queueTop = 48 + rowHeight * 7;
            ObservatoryUi.Place(composition.rectTransform, Vector2.up, new Vector2(12, -queueTop), new Vector2(262, 27));
            ObservatoryUi.Place((RectTransform)trainingQueueScroll.transform, Vector2.up,
                new Vector2(12, -queueTop - 30), new Vector2(262, panelHeight - queueTop - 40));
            float width = Mathf.Min(720, size.x - 588);
            ObservatoryUi.Place(roster, Vector2.zero, new Vector2(270, 16), new Vector2(width, 118));
            float cell = (width - 20) / 7;
            for (int index = 0; index < rosterLabels.Length; index++)
            {
                float x = 10 + index % 7 * cell;
                float y = 26 + index / 7 * 43;
                ObservatoryUi.Place(rosterIconRects[index], Vector2.up, new Vector2(x + (cell - 28) * 0.5f, -y), new Vector2(28, 28));
                ObservatoryUi.Place(rosterLabels[index].rectTransform, Vector2.up, new Vector2(x, -y - 27), new Vector2(cell, 16));
            }
            construction.Layout(width);
        }
    }
}
