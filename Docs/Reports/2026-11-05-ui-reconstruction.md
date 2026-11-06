# 시안 기반 UI 재구성 결과

16개 씬·상황별 시안을 참고해 현재 uGUI에 남색 패널, 금색 테두리, 청록색 선택 상태와 원소·시설 그림을 적용했다. 타이틀·방 찾기·방 만들기·대기실·설정·플레이어 현황·상성표·공통 안내 창과 전장 HUD가 공통 스타일을 사용한다.

시설을 모델로 제작한다는 결정은 배경과 시설 아이콘에 반영했다. 월드에 배치할 실제 3D 모델·VFX 제작은 이번 변경 범위에 포함하지 않았다.

## 변경 내용

- `ObservatoryUi`가 기존 버튼·입력창·드롭다운·스크롤의 스타일과 공용 그림을 제공한다. 기존 uGUI/TMP/Input System과 Noto Sans KR를 유지했다.
- 타이틀은 왼쪽에 닉네임과 시작·상성표·설정·종료를, 오른쪽에 시설 중심의 우주 배경을 배치했다.
- 대기실은 왼쪽 참가자 목록과 오른쪽 원소 아이콘 선택으로 구성했다. 최대 15명, 16자 한글 닉네임, 32자 방 제목을 검증 자료로 사용했다.
- 설정은 음향·화면·표현·조작 키 탭으로 나눴다. 기존 설정 저장, 화면 설정 확인·되돌리기, 키 중복 검사와 입력 대기를 유지했다.
- 전장 HUD는 상단 자원·인구, 왼쪽 아래 미니맵, 오른쪽 양성 목록·예약 취소, 아래 원소 구성·시설 건설·집결 명령으로 배치했다. 양성과 건설은 기존 서버 상태·JSON 비용·해금 조건을 표시한다.
- 배경 한 장과 원소 14종·시설 6종 아이콘을 생성했다. 생성 프롬프트와 참조는 [아트 기록](../Art/2026-11-05-ui-assets.json)에 보존했다. 외부 패키지는 추가하지 않았다.

공용 그림 설정은 `client/Assets/_Project/Resources/UI/LegionUiArt.asset`이다. `Elemental Legion/UI/Configure Concept Art` 메뉴는 원본 PNG를 가져오고 기존 스프라이트 파일 ID를 보존해 참조를 갱신한다. 기존 씬·프리팹을 재생성하지 않는다.

## 검증 결과

| 검증 | 결과 | 근거 |
| --- | --- | --- |
| Unity 6000.6.0f1 컴파일 | 오류 0, 경고 0 | [컴파일 결과](../../.artifacts/ui-reconstruction/unity-compile-final.json) |
| Unity 전체 EditMode | 25/25 통과 | [테스트 결과](../../.artifacts/ui-reconstruction/unity-full-final.json) |
| .NET 솔루션 빌드 | 오류 0, 경고 0 | [빌드 로그](../../.artifacts/ui-reconstruction/dotnet-build.log) |
| .NET 전체 테스트 | 307/307 통과, 건너뜀 0 | [테스트 로그](../../.artifacts/ui-reconstruction/dotnet-final.log), [TRX](../../.artifacts/ui-reconstruction/ui-final.trx) |
| 화면 경계 검사 | 117개 렌더링, 화면 밖 조작 요소 0개 | [검사 결과](../../.artifacts/ui-reconstruction/layout-matrix-final.json) |
| 자산 참조·메타 파일 | 새 자산 메타 누락 없음, 기존 프리팹 참조 검사 통과 | EditMode·파일 검사 |
| 사용자 씬 보존 | Bootstrap 씬 1개, 수정 없음, Play 꺼짐 | [편집기 상태](../../.artifacts/ui-reconstruction/editor-state-final.json) |

화면 검사는 타이틀·방 목록·방 만들기·대기실·설정 4종·현황·ESC·패배·전장 HUD·건설의 13종을 1280×720, 1920×1080, 2560×1440 및 UI 90·100·110%에서 수행했다. 실제 UI를 임시 추가 씬에 구성해 카메라로 렌더링하고, 활성 조작 요소의 화면 경계를 검사했다. 원래 씬은 저장하지 않았다.

117개 전체에 경계 검사를 적용했으며, 타이틀·설정·15명 대기실·HUD·건설 화면을 직접 열어 확인했다. 경계 검사만으로 모든 글자의 가독성이나 실제 마우스·키보드 조작이 검증되는 것은 아니다. HUD 미리보기의 중앙 우주 그림은 배치 확인용 배경이며 실제 전투 월드 캡처가 아니다.

새 UI 회귀 테스트 6개는 그림 참조 완전성, 아이콘 좌표, 닉네임 검증·시작 콜백, 양성 버튼·설명 높이·명령 중복, 미니맵 텍스처 정리, 만원 대기실의 접속 복구 중 원소 선택 잠금을 검증한다. 자산 누락·기존 넓은 양성 패널·아이콘 좌표·텍스처 정리·설명 높이 문제의 실패를 확인한 뒤 수정했다.

독립 코드 리뷰에서 지적된 아이콘 재사용 좌표와 타이틀 잘림, 미리보기 글꼴 문제를 수정했다. 추가 육안 확인에서 양성 설명의 줄 간격을 조정하고 입력 안내문 대비를 높였다. 편집기 AssetDatabase를 갱신한 뒤 최신 작업 트리의 런타임 UI 실행 취소 테스트도 통과했다.

## 확인한 화면

- [타이틀, 1280×720 / 100%](../../.artifacts/ui-reconstruction/previews/Title-1280x720-100.png)
- [만원 대기실, 1280×720 / 110%](../../.artifacts/ui-reconstruction/previews/Lobby-1280x720-110.png)
- [화면 설정, 1280×720 / 110%](../../.artifacts/ui-reconstruction/previews/SettingsVideo-1280x720-110.png)
- [전장 HUD 배치, 1280×720 / 110%](../../.artifacts/ui-reconstruction/previews/Hud-1280x720-110.png)
- [시설 건설 배치, 1280×720 / 110%](../../.artifacts/ui-reconstruction/previews/Construction-1280x720-110.png)

캡처 도구는 `Elemental Legion/UI/Capture Menu Previews` 메뉴와 `UiPreviewRenderer.Capture(page, width, height, percent)`다. 네트워크 접속이나 Play 진입 없이 실제 화면 구성 코드를 사용한다. 캡처·테스트 로그는 Git 추적 대상에서 제외된 `.artifacts/ui-reconstruction/`에 있다.

## 남은 검증 범위

사람이 Play에서 마우스·키보드 포커스, 대기실 준비·시작, 실제 전투 중 양성·건설·집결·채팅, 설정 적용·복원과 접속 복구 흐름을 확인해야 한다. Windows Player 패키징과 실제 경기의 프레임·메모리 측정은 수행하지 않았다. 따라서 성능 개선이나 최종 게임 화면 완성을 주장하지 않는다.

이번 UI 작업에서는 게임 밸런스·프로토콜·서버 판정을 수정하지 않았다. 함께 작업 중인 서버 인계·게임 동작·런타임 UI 컨트롤 변경은 보존했다. 커밋·푸시는 수행하지 않았다.

## UI 피드백 반영 (2026-11-05)

대기실 참가자 목록에서 빈 슬롯의 글자와 배경을 제거하고, 제목 옆에 AI를 포함한 `현재 인원 / 정원`을 표시했다. 참가자 수가 줄었다가 다시 늘어날 때 행과 인원수가 함께 복원되는지 확인했다.

건물 설명창은 제목·본문·상태를 줄바꿈하고, 실제 글 높이에 맞춰 설명창 높이를 계산한다. 공통 `BuildingTooltip`을 수정해 건설 메뉴와 전장 건물 호버에 함께 적용했다. 내용이 바뀔 때만 크기를 다시 계산하며 화면 경계 안에 배치하는 기존 처리를 유지했다.

- 회귀 테스트에서 수정 전 8개 실패를 확인했다. 수정 후 Unity 전체 EditMode **32/32**, .NET 테스트 **307/307**이 통과했으며 .NET 빌드는 오류·경고가 없었다.
- 본진을 포함한 7종 시설의 메뉴·월드 설명에 말줄임이 없고 글이 설명창 안에 들어가는지, 화면 양쪽 모서리에서 설명창이 경계를 벗어나지 않는지 검증했다.
- 만원 대기실·사람 2명과 AI 1명의 대기실·건물 설명창을 3개 해상도와 UI 90·100·110%로 총 **27회** 렌더링했다. 화면 밖 조작 요소는 없었으며 1280×720 / 110% 결과를 직접 열어 확인했다.
- 독립 코드 리뷰에서 회귀 문제는 발견되지 않았다. 기존 uGUI/TMP의 줄바꿈·글 크기 측정을 사용했으며 외부 자산·패키지는 추가하지 않았다.

근거: [Unity 테스트](../../.artifacts/ui-reconstruction/feedback-green.json), [.NET 빌드](../../.artifacts/ui-reconstruction/feedback-dotnet-build.log), [.NET 테스트](../../.artifacts/ui-reconstruction/feedback-dotnet-test.log), [화면 경계 검사](../../.artifacts/ui-reconstruction/feedback-layout.json).

화면 확인: [빈 슬롯 없는 대기실](../../.artifacts/ui-reconstruction/previews/LobbyPartial-1280x720-110.png), [전체 내용이 보이는 건물 설명창](../../.artifacts/ui-reconstruction/previews/ConstructionTooltip-1280x720-110.png).

사람의 실제 Play 조작으로 입·퇴장 갱신과 건물 호버를 확인하는 과정은 수행하지 않았다.
