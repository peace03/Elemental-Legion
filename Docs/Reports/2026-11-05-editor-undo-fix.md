# EditMode 실패와 Editor Undo 크래시 수정

2026-11-05. Unity 6000.6.0f1, uGUI 2.6.0, `C:/Project/Elemental-Legion/client`에서 확인했다.

## 원인과 수정

1. 빌드 진입점 검사가 이전 M1의 `Legion.unity`를 기대했다. 현재 타이틀 흐름에 맞춰 첫 활성 씬이 `Bootstrap.unity`인지 검사하고, Legion 씬의 활성 포함·자산·글꼴 검사는 유지했다.
2. `ClientFlow.QuitAfterHandoff`가 Editor 조건부 코드에서 `UnityEditor.EditorApplication`을 직접 호출해 런타임 어셈블리에 Editor 의존성이 생겼다. Editor 전용 `ClientFlowQuitBridge`가 종료 요청을 받도록 옮겼다. 서버 인계 → 세션 정리 → Editor Play 종료 순서와 Player의 `Application.Quit()`는 유지한다. 런타임의 Editor 미참조 검사를 약화하지 않았다.
3. 런타임 화면이 패키지의 `TMP_DefaultControls`·`DefaultControls`로 UI를 만들었다. 설치된 패키지는 Editor에서 `ObjectFactory`·`Undo.SetTransformParent`를 사용한다. 일시적인 UI를 변경·파괴한 뒤 Test Runner가 그 Undo를 되돌리면서 네이티브 `RemoveNullChildrenAndComponents`에서 크래시했다. 입력·드롭다운·슬라이더·스크롤 목록을 일반 `GameObject`와 `SetParent(false)`로 만드는 `RuntimeUiControls`로 교체했다. 세 호출자의 기본 연결·레이아웃을 유지하며 패키지와 Unity 버전은 변경하지 않았다.

이전 크래시의 호출 경로는 다음과 같다.

```text
PerformUndoTask.Execute
  → Undo.RevertAllDownToGroup
  → UndoManager.ApplyUndo
  → ObjectUndo.ApplyUndo
  → StructureUndoBase.ApplyInternal
  → RemoveNullChildrenAndComponents
  → RecordObjectChangedInternal
```

수정 전 실제 슬라이더 생성 회귀에서는 Undo 기록이 0개에서 6개로 늘어 실패했다. 수정 후에는 슬라이더와 네 종류의 컨트롤 생성 모두 Undo 기록을 늘리지 않는다. 별도의 정상 Undo 변경을 기록·복원하는 양성 대조 검사로 진단 자체도 검증했다. 사용자 Undo를 비우거나 테스트·크래시를 숨기는 처리는 추가하지 않았다.

추가한 두 소스의 `.meta` GUID가 잘못되어 Unity가 파일을 건너뛰던 문제도 발견했다. 유효한 32자리 GUID로 바꾸고 명시적으로 가져온 뒤 실제 최신 어셈블리가 컴파일된 것을 확인했다. 기존 자산 GUID는 변경하지 않았다.

## 검증

- Unity 컴파일 오류 0개.
- Undo·컨트롤 참조·입력 콜백 회귀 3개 통과.
- 전체 EditMode 25/25를 같은 Editor(PID 37432)에서 3회 연속 통과했다. 실행 시간은 0.64·0.71·0.70초이며 각 실행 뒤 연결 상태가 `ready`였다. 마지막 확인 13:11:18에도 살아 있었고, 새 크래시 기록이 없었다.
- .NET 전체 빌드 경고 0, 오류 0, 테스트 307/307 통과.
- 실제 Play 조작과 Windows Player 실행은 수행하지 않았다.

원본 검증 결과: [실패 재현](Artifacts/editor-undo-fix/undo-red.json), [Undo 회귀](Artifacts/editor-undo-fix/undo-green.json), [전체 1회](Artifacts/editor-undo-fix/editmode-1.json), [전체 2회](Artifacts/editor-undo-fix/editmode-2.json), [전체 3회](Artifacts/editor-undo-fix/editmode-3.json), [Editor 상태](Artifacts/editor-undo-fix/editor-after-tests.json), [.NET 테스트](Artifacts/editor-undo-fix/dotnet-tests.log).

## 조사 자료

새 패키지나 외부 코드를 도입하지 않았다. 기존 설치본의 생성 경로를 확인하고, 현재 게임 UI가 사용하는 연결 계약에 맞춰 작은 생성 함수를 작성했다.

- [Unity 공식 동적 UI 구성 안내](https://github.com/Unity-Technologies/uGUI/blob/main/com.unity.ugui/Documentation~/HOWTO-UICreateFromScripting.md): 일반 생성·인스턴스와 부모 연결 방식 참고.
- [Unity 공식 팩터리 주의사항](https://docs.unity.cn/Packages/com.unity.ugui%402.6/api/UnityEngine.UI.DefaultControls.IFactoryControls.html): Undo 부모 변경 뒤 UI 컴포넌트 추가와 RectTransform의 크래시 위험.
- [설치본과 비교한 TMP 생성 소스](https://github.com/Unity-Technologies/uGUI/blob/main/com.unity.ugui/Runtime/TMP/TMP_DefaultControls.cs): Editor 조건부 ObjectFactory·Undo 경로 확인. 상류 링크는 조사 참고이며 실제 수정·검증 기준은 설치된 `com.unity.ugui@23caec89ae27`이다.
