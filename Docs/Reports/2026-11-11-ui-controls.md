# 전장 HUD와 분대 단축키 구현

2026-11-11 · `codex/m0-simulation`

분대 키를 단독으로 눌렀다 떼면 선택과 카메라 이동을 한 번 확정한다. 1~4 중 출발 키를 누른 채 목적 키를 누르면 현재 조종과 카메라를 유지하면서 빠른 전송을 요청한다. 서버는 출발 분대에 합류한 생존 원소 중 최대 3기를 보내고, 실제 출발 수와 거절 사유를 요청자의 HUD에 표시한다. 원소는 현재 위치에서 걸어가며 도착할 때 정식으로 합류한다.

자원과 본진 상태, 분대 버튼, 양자 이동, 번호가 있는 미니맵은 기본 HUD에 표시한다. 양성·병력·건설은 같은 오른쪽 영역에 하나씩 열린다. 건물·원소 소개는 관측 범위의 대상을 가리키거나 클릭해 연다. 현황판에 내 군단과 공개 총점을 배치했고, 여정 기록은 해당 탭을 실제로 보고 현황판을 닫을 때 확인 처리한다. 기존 설정값과 키 재설정은 그대로 읽으며, 숫자를 입력하는 병력 수량 칸에서는 분대 키 해석을 잠시 멈춘다.

## 검증 결과

| 대상 | 확인 결과 |
| --- | --- |
| .NET 빌드 | `dotnet build server/ElementalLegion.sln -m:1 -nodeReuse:false` 성공. 경고 0개, 오류 0개. |
| .NET 테스트 | `dotnet test server/ElementalLegion.sln --no-build --no-restore -m:1 -nodeReuse:false -- xUnit.ParallelizeTestCollections=false`로 829/829 통과. 처음에는 샌드박스에서 TLS 시험 인증서 생성이 거부됐고, 권한을 갖춘 병렬 실행에서는 방 목록 조회 1건이 실패했다. 직렬 실행에서 동일한 시간 제한으로 전체를 재검사했다. [직렬 결과](../../.artifacts/ui-controls/dotnet-serial.trx) |
| Unity | `client/`의 6000.6.0f1 Editor에서 C# 컴파일 경고 0개, 오류 0개. `ElementalLegion.Client.EditModeTests` 207/207 통과. 설정한 NumPad 조합, 빠른 전송 후 선택과 카메라 유지, 창 상태와 대기열, 미니맵 표시와 입력을 포함한다. [EditMode 결과](../../.artifacts/ui-controls/editmode-verified.json) |
| 화면 | 실제 프리팹과 정적 TMP 글꼴로 720p·1080p·1440p 및 UI 90·100·110%의 16개 화면·상태를 렌더링했다. 720p·110%의 에너지 양성, 여정, 상성표, 설정 탭 6장을 포함해 150장이다. 화면 밖 컨트롤과 누락 글리프는 각각 0건이었다. 2인 방 규칙으로 만든 고정 시드 서버 관측을 썼고, 본진 경보 장면은 HUD에 경보를 직접 전달했다. [캡처 목록](../../.artifacts/ui-controls/matrix-summary.json) |
| 미니맵 | 같은 2인 방 관측에서 이동 점의 시간만 60Hz로 진행시켰다. 2초 동안 밝기에 따른 메시 갱신 요청은 24회였고, 동작 줄이기를 켜고 표시가 안정된 뒤에는 0회였다. 244개 정점의 메시를 버퍼를 재사용해 3,000회 생성했을 때 관리 힙 할당은 0바이트, 평균 32.3µs였다. [Editor 측정 결과](../../.artifacts/ui-controls/map-probe.json) |

720p·UI 110% 캡처를 보고 양성의 수량·목적 라벨, 귀환 시전 상태, 소개 카드의 제목과 닫기 버튼을 조정했다. 현황판에 한글 닉네임을 표시할 때는 기존 Noto Sans KR에서 필요한 글리프를 읽는 대체 글꼴을 연결했다. 기본 HUD 다섯 영역의 사각형 면적 합은 1920×1080·UI 100%에서 화면의 약 13%다.

- [기본 HUD](../../.artifacts/ui-controls/previews/Hud-1920x1080-100.png) · [4→2 출격 결과](../../.artifacts/ui-controls/previews/QuickTransfer-1280x720-110.png) · [잠긴 건설 상세](../../.artifacts/ui-controls/previews/ConstructionLocked-1280x720-110.png)
- [양성 예약](../../.artifacts/ui-controls/previews/TrainingQueue-1280x720-110.png) · [양자 이동 시전](../../.artifacts/ui-controls/previews/RecallCasting-1280x720-110.png) · [ESC 메뉴](../../.artifacts/ui-controls/previews/Pause-1280x720-110.png)

## 접속과 저장

클라이언트와 서버는 프로토콜 **23** 및 같은 게임 데이터를 사용한다. 빠른 전송 패킷 번호는 40이다. 데이터에 `legion.squads.quickTransferCount = 3`을 추가해 LF 정규화 SHA-256이 `aec08f28505008e20508e667421aa618fdf9cd9cbffd63a809c7a7f264adb344`로 바뀌었다. 기존 Format 2·Schema 3 월드는 원본 JSON과 저장 해시가 일치하고 새 수량 키만 추가된 경우 명시적 변환으로 새 경로에 복원할 수 있다. 테스트용 저장과 백업에서 왕복을 확인했다. [프로토콜](../protocol.md) · [월드 저장 변환](../../README.md#구형-월드-저장-변환)

## 재사용한 기반

- 키의 누름·해제는 프로젝트에 이미 설치된 Input System의 `ButtonControl`을 사용한다. HUD와 포인터 판정은 기존 uGUI·TMP, `LegionUiArt`, 서버의 시야 관측과 증원 상태를 확장했다. [Unity Input System](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.20/api/UnityEngine.InputSystem.Controls.ButtonControl.html) · [uGUI Graphic](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/api/UnityEngine.UI.Graphic.html)
- OpenRA의 분대 선택 인터페이스와 GPL 고지를 참고했다. Asset Store의 Fast RTS Minimap 1.2는 등록된 URP 호환성과 현재 서버 시야를 연결하는 비용을 검토했다. 분대 입력과 미니맵은 프로젝트 내부 코드와 자산으로 구현했다. [OpenRA 인터페이스](https://github.com/OpenRA/OpenRA/blob/bleed/OpenRA.Game/Traits/TraitsInterfaces.cs) · [OpenRA COPYING](https://github.com/OpenRA/OpenRA/blob/bleed/COPYING) · [Fast RTS Minimap](https://assetstore.unity.com/packages/tools/gui/fast-rts-minimap-237367)

## 남은 확인

실제 Play의 키보드·마우스와 채팅·포커스 전환, Player 빌드, 플레이 중 전체 프레임과 할당은 미검증이다. 운영 월드는 이관을 실행하지 않았다. [구현 계획과 실플레이 시나리오](../Plans/2026-11-11-ui-controls.md#검증-명령과-실제-플레이-시나리오)

## 귀환과 지점 이동 후속 수정

- 귀환하는 분대의 중심은 정지하고, 합류 원소는 시전 중에도 진형 정리와 충돌 회피를 계속한다. 새 이동 명령과 피격에는 기존 취소 규칙을 적용한다. 클라이언트는 귀환을 보낼 때 이전 우클릭의 전송 대기분을 버린다. [귀환 규칙](../Rules/gameplay.md)
- 전장·미니맵에서 보낸 지점 이동은 클릭 위치에 청록색 고리를 띄운다. 고리는 기본 0.55초 동안 작아지며 사라진다. 상단 좌표는 우클릭 입력 동안 표시하고, 버튼을 놓으면 지운다. 미니맵 점선은 자기 분대의 현재 위치에서 서버가 확정한 목적지를 가리킨다. 도착하거나 명령이 바뀌면 점선을 지운다.
- .NET 빌드는 경고·오류 없이 성공했고 전체 테스트 840개를 통과했다. 시전 중 원소가 실제로 움직이고 예정된 틱에 귀환하는 사례를 추가했다. [테스트 결과](../../.artifacts/recall-movement-feedback/recall-movement-feedback.trx)
- Unity 6000.6.0f1 Editor에서 컴파일 경고·오류 0개와 EditMode 216개 통과를 확인했다. 기존 Play가 진행되는 동안 별도 복사본에서 그래픽 장치를 끄고 실행한 배치 검사는 렌더 테스트 세 건이 실패했다. 이후 Play가 끝난 Editor에서 전체를 다시 실행해 통과했다. [배치 결과](../../.artifacts/recall-movement-feedback/unity-editmode.xml) · [최종 EditMode 결과](../../.artifacts/recall-movement-feedback/editmode.json)
- 서버 시뮬레이션의 자기 분대 명령과 실제 프리팹을 1280 × 720으로 렌더링했다. 입력 해제 뒤 좌표가 사라지고 점선은 유지됐다. 분대가 도착해 서버 상태가 대기로 바뀐 화면에서는 점선도 사라졌다. 실제 Play 조작과 Player 빌드는 미검증이다. [입력 중](../../.artifacts/recall-movement-feedback/01-pressed.png) · [입력 해제](../../.artifacts/recall-movement-feedback/02-released.png) · [도착](../../.artifacts/recall-movement-feedback/03-arrived.png)

2026-11-11에 OpenRA의 명령 목적지 표시와 Waypoint Indicators의 공개 소개를 확인했다. 이번 구현은 기존 전장 링과 미니맵의 점선 그리기를 재사용한다. [OpenRA 이동 활동](https://github.com/OpenRA/OpenRA/blob/bleed/OpenRA.Mods.Common/Activities/Move/Move.cs) · [Waypoint Indicators](https://assetstore.unity.com/packages/tools/gui/waypoint-indicators-153472)
