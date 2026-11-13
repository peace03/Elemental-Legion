using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ElementalLegion.Client.Input
{
    /// <summary>기존 Player/Move 액션을 인스턴스별로 복제해 사용한다. 원본 바인딩은 변경하지 않는다.</summary>
    public sealed class LegionInput : MonoBehaviour
    {
        private const int MaxBufferedSquadActions = 16;
        [SerializeField, Tooltip("Player/Move가 있는 프로젝트 Input Actions 원본입니다.")]
        private InputActionAsset actions;
        private InputActionAsset instance;
        private InputAction move;
        private readonly SquadShortcutState squadShortcut = new SquadShortcutState();
        private readonly Queue<SquadShortcutResult> squadActions = new Queue<SquadShortcutResult>(MaxBufferedSquadActions);
        private Keyboard squadKeyboard;
        private bool squadInputAllowed, listening;
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
            ResetSquadShortcut();
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
            if (!allowed || Controls == null || !isActiveAndEnabled)
            {
                ResetSquadShortcut();
                return default;
            }
            if (!listening)
            {
                InputSystem.onEvent += ObserveSquadInput;
                listening = true;
            }
            if (!squadInputAllowed || squadKeyboard != Keyboard.current)
            {
                squadInputAllowed = true;
                squadKeyboard = Keyboard.current;
                squadActions.Clear();
                squadShortcut.Reset();
                byte held = 0;
                for (int slot = ControlBindings.Squad1; slot <= ControlBindings.AllField; slot++)
                    if (Controls.Held(slot)) held |= SquadBit(slot);
                squadShortcut.Update(held, 0, 0, true);
            }
            return squadActions.Count > 0 ? squadActions.Dequeue() : default;
        }

        internal void ResetSquadShortcut()
        {
            squadShortcut.Reset();
            squadActions.Clear();
            squadInputAllowed = false;
            squadKeyboard = null;
        }

        private void ObserveSquadInput(InputEventPtr evt, InputDevice device)
        {
            if (!squadInputAllowed || !(device is Keyboard keyboard) || !device.enabled || evt.handled ||
                !(evt.IsA<StateEvent>() || evt.IsA<DeltaStateEvent>())) return;

            // 키 순서를 지키려면 입력 시스템이 상태를 적용하기 전에 각 이벤트의 앞뒤를 비교한다.
            byte before = 0, after = 0;
            for (int slot = ControlBindings.Squad1; slot <= ControlBindings.AllField; slot++)
            {
                var key = keyboard[Controls[slot]];
                byte bit = SquadBit(slot);
                bool wasHeld = key.isPressed;
                if (wasHeld) before |= bit;
                if (key.ReadValueFromEvent(evt, out float value) ? key.IsValueConsideredPressed(value) : wasHeld)
                    after |= bit;
            }
            if (before == after) return;
            if (keyboard != squadKeyboard)
            {
                squadKeyboard = keyboard;
                squadActions.Clear();
                squadShortcut.Reset();
                squadShortcut.Update(before, 0, 0, true);
            }
            var result = squadShortcut.Update(after, (byte)(after & ~before), (byte)(before & ~after), true);
            if (result.TargetSquad == 0 && result.SelectionMask == 0) return;
            if (squadActions.Count < MaxBufferedSquadActions) squadActions.Enqueue(result);
            else ResetSquadShortcut();
        }

        private static byte SquadBit(int slot) => (byte)(1 << (slot - ControlBindings.Squad1));

        public void Release()
        {
            ResetSquadShortcut();
            if (listening)
            {
                InputSystem.onEvent -= ObserveSquadInput;
                listening = false;
            }
            if (instance == null) return;
            instance.Disable(); Destroy(instance); instance = null; move = null; appliedRevision = -1;
        }

        private void OnDisable() => Release();
    }
}
