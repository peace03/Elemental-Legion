using System;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using TMPro;
using UnityEngine;

namespace ElementalLegion.Client.UI
{
    /// <summary>자기 관측 상태만 표시하고 양성·집결 의도를 전달하는 수동 HUD이다.</summary>
    public sealed class CombatHud : MonoBehaviour
    {
        [SerializeField,Tooltip("접속 중에만 표시하는 전투 패널입니다.")] private GameObject content;
        [SerializeField,Tooltip("핵·현재/예약/최대 인구·수비대 표시입니다.")] private TMP_Text economy;
        [SerializeField,Tooltip("원소 구성과 양성 진행 표시입니다.")] private TMP_Text composition;
        [SerializeField,Tooltip("명령 처리 결과 표시입니다.")] private TMP_Text feedback;
        [SerializeField,Tooltip("14종 양성 버튼입니다. 공유 원소 순서를 따릅니다.")] private UnityEngine.UI.Button[] train;
        [SerializeField,Tooltip("이름·비용·시간·잠금 이유 표시입니다.")] private TMP_Text[] labels;
        [SerializeField,Tooltip("수비대를 집결하는 버튼입니다.")] private UnityEngine.UI.Button rally;
        private GameRules rules;
        private string[] names;
        private bool subscribed;
        public event Action<int> TrainRequested;
        public event Action RallyRequested;
        public void Configure(GameDataDocument document,GameRules gameRules)
        {
            if(content==null||economy==null||composition==null||feedback==null||rally==null||train.Length!=gameRules.Elements.Count||labels.Length!=train.Length)
                throw new InvalidOperationException("CombatHud 필수 참조가 없습니다.");
            rules=gameRules;names=new string[train.Length];
            for(int index=0;index<train.Length;index++)
            {
                int element=index;names[index]=(string)document.Root["elements"][index]["displayName"];
                if(!subscribed)train[index].onClick.AddListener(()=>TrainRequested?.Invoke(element));
            }
            if(!subscribed)rally.onClick.AddListener(()=>RallyRequested?.Invoke());subscribed=true;ResetHud();
        }
        public void Apply(GameplayFrame frame)
        {
            content.SetActive(true);
            economy.text=$"핵 {frame.CoreBalance}    인구 {frame.Population} + 예약 {frame.ReservedPopulation} / {frame.PopulationCap}    수비대 {frame.GarrisonCount}";
            var text=new System.Text.StringBuilder();
            for(int index=0;index<names.Length;index++)if(frame.ElementCounts[index]>0)text.Append(names[index]).Append(' ').Append(frame.ElementCounts[index]).Append("   ");
            text.Append('\n');
            if(frame.TrainingElement>=0)
            {
                double seconds=Math.Max(0,(frame.TrainingEndTick-frame.Tick)*rules.DeltaTime);
                float progress=frame.TrainingEndTick==frame.TrainingStartTick?1:(float)(frame.Tick-frame.TrainingStartTick)/(frame.TrainingEndTick-frame.TrainingStartTick);
                text.Append("양성: ").Append(names[frame.TrainingElement]).Append(' ').Append(Mathf.Clamp01(progress).ToString("P0")).Append(" · ").Append(seconds.ToString("F1")).Append("초");
            }
            else text.Append("양성 대기 · G: 수비대 집결");
            composition.text=text.ToString();rally.interactable=frame.GarrisonCount>0;
            bool natureForge=false,energyForge=false;
            foreach(BuildingView building in frame.Buildings)if(building.OwnerId==frame.PlayerId)
            {natureForge|=building.Kind==BuildingKind.NatureForge;energyForge|=building.Kind==BuildingKind.EnergyForge;}
            for(int index=0;index<train.Length;index++)
            {
                bool nature=rules.Elements[index].Group==ElementGroup.Nature;int cost=rules.TrainingCost(index,frame.MainElement);
                bool unlocked=rules.IsUnlocked(frame.BaseLevel,nature?"forge_nature":"forge_energy")&&(nature?natureForge:energyForge);
                string reason=frame.TrainingElement>=0?"진행 중":frame.Population+frame.ReservedPopulation>=frame.PopulationCap?"인구 상한":!unlocked?"미해금 · 양성소 필요":frame.CoreBalance<cost?"핵 부족":"양성";
                train[index].interactable=reason=="양성";
                labels[index].text=$"{names[index]}  ·  {cost}핵\n{(nature?rules.NatureTrainSeconds:rules.EnergyTrainSeconds):0.#}초  |  {reason}";
            }
        }
        public void CommandFailed(CommandFailure failure)=>feedback.SetText(failure==CommandFailure.InsufficientCores?"핵이 부족합니다.":failure==CommandFailure.PopulationCap?"인구 상한에 도달했습니다.":failure==CommandFailure.Busy?"이미 양성 중입니다.":failure==CommandFailure.NotUnlocked?"원소 또는 양성소가 해금되지 않았습니다.":"명령 실패");
        public void Admission(CommandAdmission admission)=>feedback.SetText(admission==CommandAdmission.Accepted?"":$"명령 접수 실패: {admission}");
        public void ResetHud(){if(content!=null)content.SetActive(false);if(feedback!=null)feedback.text=string.Empty;}
        private void OnDestroy(){if(train!=null)foreach(var button in train)if(button!=null)button.onClick.RemoveAllListeners();if(rally!=null)rally.onClick.RemoveAllListeners();}
    }
}
