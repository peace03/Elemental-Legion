# 한 원소 위에 다른 원소 본체가 겹치는 현상 수정

사용자가 지적한 문제는 대열 간격이 아니라 **한 유닛의 자리에서 여러 원소 본체가 포개져 보이는 현상**이다. [제보 화면](Artifacts/unit-status-overlap/user-report.png)을 기준으로 확인했다.

## 원인과 변경

`ElementalVisuals.Render`는 보호막이 있으면 자기장 원소의 전체 메시·재질을, 빙결 상태이면 얼음 원소의 전체 메시·재질을 추가로 그렸다. 따라서 물에는 자기장이나 얼음 본체가 겹쳤고, 자기장에는 자기장 본체가 두 벌 나타날 수 있었다. 일반 본체를 두 번 제출하거나 본체 입자가 전체 물 메시를 복제하는 경로는 확인되지 않았다.

상태 효과에는 기존 중립 구체 메시와 전용 `ElementStatusShell` 셰이더를 사용한다. 보호막은 가는 에너지 테두리, 빙결은 폭이 조금 넓은 흰 균열 테두리를 표시한다. 중앙은 투명하게 두어 원래 본체를 유지한다. 외피는 해당 유닛의 본체 경계와 현재 표시 반경을 따른다. 유령 제외, 상태 배치 용량, 시야 해제 경로를 유지했다.

게임 프리팹과 독립 갤러리 씬에 재질을 연결했고, `M3SceneSetup`, `M3GalleryBuilder`, `IngameVisualArtSetup`에서 자산을 다시 구성해도 연결되도록 했다. 원소 본체·입자 구성, 유닛 간격·표시 크기·카메라·서버 판정·게임 수치는 변경하지 않았다.

## 검증

- 수정 전 회귀 검사 2개가 상태 효과의 다른 원소 메시 참조에서 실패했다. 같은 실행의 기존 검사 170개는 통과했다.
- 수정 후 Unity EditMode **173/173** 통과. 최종 셰이더 조정·재임포트 후 상태 검사 **5/5**를 다시 통과했고 셰이더 오류는 0개였다.
- 회귀 검사는 물·암흑 물질의 상태 효과가 다른 원소 본체 참조 없이 렌더되는지, 전용 재질과 중립 메시를 사용하는지 확인한다. 기존 유령 제외·4,096기 상태 배치·입자 상한 검사도 통과했다.
- [수정 전](Artifacts/unit-status-overlap/before.png)과 [수정 후](Artifacts/unit-status-overlap/after.png)는 실제 Unity 렌더다. 위부터 암흑 물질·자기장·물, 왼쪽부터 일반·보호막·빙결이다. 각각 9기·입자 102개다. 시간에 따라 본체가 회전하므로 두 이미지의 자세는 다르다. 수정 전 중앙 흰 원은 군단 중심 마커이며 최종 비교에서만 숨겼다.
- 원래 `Bootstrap` 씬을 복구했고, 씬 1개·미저장 변경 없음·Play 종료 상태를 확인했다. 자료 요약은 [validation.json](Artifacts/unit-status-overlap/validation.json)에 있다.

새 패키지나 외부 자산은 도입하지 않았다. 기존 구체·배치 렌더·Editor 자산 구성 방식을 재사용했다. 셰이더 구조는 [Unity 공식 URP 안내](https://docs.unity.com/en-us/engine/6000.6/manual/materials-and-shaders/shaders/writing-custom-shaders-urp/writing-shaders-urp/basic-unlit-structure)를 참고했고, 속성은 배치 전체가 공유하는 일반 uniform으로 구성했다.

실제 인터넷 방의 Play, Windows Player 빌드와 GPU 프레임 성능은 이번 검증에 포함하지 않았다.
