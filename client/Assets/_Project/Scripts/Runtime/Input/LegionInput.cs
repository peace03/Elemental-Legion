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
        private readonly SquadShortcutState squadShortcut = new SquadShortcutState();
        private int appliedRevision = -1;
        public ControlBindings Controls { get; private set; }
        public InputActionAsset Actions => actions;
        internal byte PendingFromSquad => squadShortcut.PendingFromSquad;

        public void Initialize()
        {
            if (instance != null) return;
            if (actions == null) throw new InvalidOperationException("LegionInput.actions 참조가 없습니다.");
            instance = Instantiate(actions);
            move = instance.FindAction("Player/Move", true);
            Apply(Controls ?? ControlBindings.Load());
        }

        public void Apply(ControlBindings controls)
        {
            if (Controls == controls && appliedRevision == controls.Revision) return;
            squadShortcut.Reset();
            Controls = controls;
            if (move == null) return;
            controls.Apply(move);
            appliedRevision = controls.Revision;
        }

        public System.Numerics.Vector2 Read(bool allowed)
        {
            if (!allowed || move == null) return System.Numerics.Vector2.Zero;
            Vector2 value = move.ReadValue<Vector2>();
            return new System.Numerics.Vector2(value.x, value.y);
        }

        internal SquadShortcutResult ReadSquadShortcut(bool allowed)
        {
            byte held = 0, pressed = 0, released = 0;
            if (Controls != null)
            {
                for (int slot = ControlBindings.Squad1; slot <= ControlBindings.AllField; slot++)
                {
                    byte bit = (byte)(1 << (slot - ControlBindings.Squad1));
                    if (Controls.Held(slot)) held |= bit;
                    if (Controls.Pressed(slot)) pressed |= bit;
                    if (Controls.Released(slot)) released |= bit;
                }
            }
            return squadShortcut.Update(held, pressed, released, allowed && Controls != null);
        }

        internal void ResetSquadShortcut() => squadShortcut.Reset();

        public void Release()
        {
            squadShortcut.Reset();
            if (instance == null) return;
            instance.Disable(); Destroy(instance); instance = null; move = null; appliedRevision = -1;
        }

        private void OnDisable() => Release();
    }
}
