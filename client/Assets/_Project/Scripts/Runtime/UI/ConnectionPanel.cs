using System;
using System.Collections.Generic;
using ElementalLegion.Shared.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ElementalLegion.Client.UI
{
    /// <summary>접속 화면은 표시와 입력 전달만 맡는다. 소켓과 게임 상태의 소유권은 시작 컴포넌트에 있다.</summary>
    public sealed class ConnectionPanel : MonoBehaviour
    {
        [SerializeField, Tooltip("서버 주소 입력입니다.")] private TMP_InputField host;
        [SerializeField, Tooltip("시작 자연 원소 선택입니다.")] private TMP_Dropdown element;
        [SerializeField, Tooltip("접속 버튼입니다.")] private Button join;
        [SerializeField, Tooltip("접속 종료 버튼입니다.")] private Button leave;
        [SerializeField, Tooltip("연결 상태 표시입니다.")] private TMP_Text status;
        public event Action<string, int> ConnectRequested;
        public event Action DisconnectRequested;
        public bool BlocksMovement => host != null && host.isFocused || element != null && element.IsExpanded;

        public void Configure(IReadOnlyList<string> elementNames)
        {
            if (host == null || element == null || join == null || leave == null || status == null) throw new InvalidOperationException("ConnectionPanel 필수 참조가 없습니다.");
            element.ClearOptions(); var options = new List<string>(elementNames.Count);
            for (int index = 0; index < elementNames.Count; index++) options.Add(elementNames[index]);
            element.AddOptions(options);
            Show(ConnectionState.Disconnected, string.Empty);
        }

        public void Show(ConnectionState state, string detail)
        {
            bool idle = state == ConnectionState.Disconnected;
            bool connected=state==ConnectionState.Connected;
            foreach(string child in new[]{"Subtitle","HostLabel","ServerAddress","ElementLabel","StartingElement","Join"})
            {Transform target=transform.Find(child);if(target!=null)target.gameObject.SetActive(!connected);}
            var rect=(RectTransform)transform;rect.sizeDelta=new Vector2(rect.sizeDelta.x,connected?116:440);
            if(leave!=null)((RectTransform)leave.transform).anchoredPosition=connected?new Vector2(228,-62):new Vector2(228,-302);
            if(status!=null){var statusRect=(RectTransform)status.transform;statusRect.anchoredPosition=connected?new Vector2(24,-66):new Vector2(24,-364);statusRect.sizeDelta=connected?new Vector2(196,42):new Vector2(392,64);}
            host.interactable = idle; element.interactable = idle; join.interactable = idle; leave.interactable = !idle;
            status.text = detail.Length > 0 ? detail : state == ConnectionState.Connected ? "연결됨 · 방향키 또는 WASD로 이동" : state == ConnectionState.Connecting ? "서버에 연결 중…" : "서버 주소와 시작 원소를 선택하세요.";
        }

        private void OnEnable() { join.onClick.AddListener(Join); leave.onClick.AddListener(Leave); }
        private void OnDisable() { join.onClick.RemoveListener(Join); leave.onClick.RemoveListener(Leave); }
        private void Join() => ConnectRequested?.Invoke(host.text.Trim(), element.value);
        private void Leave() => DisconnectRequested?.Invoke();
    }
}
