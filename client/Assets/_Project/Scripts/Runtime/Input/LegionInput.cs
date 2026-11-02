using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ElementalLegion.Client.Input
{
    /// <summary>기존 Player/Move 액션을 인스턴스별로 복제해 사용한다. 원본 바인딩은 변경하지 않는다.</summary>
    public sealed class LegionInput : MonoBehaviour
    {
        [SerializeField, Tooltip("Player/Move가 있는 프로젝트 Input Actions 원본입니다.")]
        private InputActionAsset actions;
        private InputActionAsset instance;
        private InputAction move;

        public void Initialize()
        {
            if (instance != null) return;
            if (actions == null) throw new InvalidOperationException("LegionInput.actions 참조가 없습니다.");
            instance = Instantiate(actions);
            move = instance.FindAction("Player/Move", true); move.Enable();
        }

        public System.Numerics.Vector2 Read(bool allowed)
        {
            if (!allowed || move == null) return System.Numerics.Vector2.Zero;
            Vector2 value = move.ReadValue<Vector2>();
            return new System.Numerics.Vector2(value.x, value.y);
        }

        public void Release()
        {
            if (instance == null) return;
            instance.Disable(); Destroy(instance); instance = null; move = null;
        }

        private void OnDisable() => Release();
    }
}
