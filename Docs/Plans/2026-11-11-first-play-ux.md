# 첫 플레이 UX 구현 계획

> 다른 채팅에서 구현할 때 아래 체크박스를 작업 순서대로 진행한다. 하단의 인계 문장을 복사하면 기획서와 시안도 함께 찾을 수 있다.

2026-11-11 · 메인 게임 · 구현 전

**목표** 처음 입장한 플레이어가 선택 분대와 가까운 대상을 보고 이동한다. 입력을 보내면 그 자리에서 요청을 확인하고, 실제 획득·합류·귀환이 확인되면 전장과 관련 HUD에서 결과를 읽는다.

**구조** `ClientBootstrap`은 기존 입력·명령 순번·수신 사건을 연결한다. `CombatHud`는 표시 상태를 관리하고, `ElementalVisuals`와 `ObservatoryMap`은 같은 관측으로 전장·미니맵을 그린다. 서버의 명령·시야·게임 데이터 계약을 그대로 사용한다.

**기술** Unity 6000.6.0f1, 기존 uGUI·TMP·Input System, `shared/`의 Gameplay·Fog 관측.

**기준** [첫 플레이 UX 기획서](../기획서/2026-11-11-first-play-ux.md)의 동작을 기존 런타임 UI로 구현한다. [시안 인덱스](../Art/Concepts/2026-11-11-first-play-ux-index.md)의 PNG는 배치와 연출을 비교하는 데 사용한다. 출발점은 [HUD·단축키 구현 기록](../Reports/2026-11-11-ui-controls.md#귀환과-지점-이동-후속-수정)과 현재 작업 트리다.

## 시안과 구현 순서

| 구현 순서 | 사용자에게 보이는 변화 | 확인할 시안 |
| --- | --- | --- |
| 0 | 기존 입력·HUD 상태와 검증 기준 확보 | [현재 HUD 캡처와 보고서](../Reports/2026-11-11-ui-controls.md) |
| 1 | 1번 선택 확인, 우클릭 요청·서버 목적지·도착 | [01 이동 요청](../Art/Concepts/2026-11-11-first-play-ux/01-movement.png) |
| 2 | 가까운 핵과 중립의 행동 표시, 내 핵 획득 피드백 | [02 이동 반영과 핵 획득](../Art/Concepts/2026-11-11-first-play-ux/02-core-receipt.png) |
| 3 | 시설 역할·비용·배치·착공·완공 | [03 건설](../Art/Concepts/2026-11-11-first-play-ux/03-construction.png) |
| 4 | 양성할 원소·수량·비용·목적과 실제 진행 | [04 양성](../Art/Concepts/2026-11-11-first-play-ux/04-training.png) |
| 5 | G 지원 진입, 빠른 전송 출발 수와 목적 분대의 증원 | [05 병력 지원](../Art/Concepts/2026-11-11-first-play-ux/05-reinforcements.png) |
| 6 | 본진 경보와 B, 양자 이동의 시전·중단·완료 | [06 경보와 시전](../Art/Concepts/2026-11-11-first-play-ux/06-base-alert-casting.png) · [07 귀환 완료](../Art/Concepts/2026-11-11-first-play-ux/07-recall-arrival.png) |
| 7 | 전체 화면·입력·실제 플레이를 같은 조건에서 점검 | [7장 인덱스](../Art/Concepts/2026-11-11-first-play-ux-index.md) |

작업 1–2를 첫 시제품으로 묶는다. 이후 관리 창과 본진 대응을 각각 연결하고 작업 7에서 함께 실행한다. 아래 경로의 `Runtime/`은 `client/Assets/_Project/Scripts/Runtime/`, `EditMode/`는 `client/Assets/_Project/Tests/EditMode/`를 뜻한다.

## 공통 기준

- 이동·획득·건설·양성·전송·귀환을 서버 관측과 본인 명령 순번으로 연결한다. `CommandAdmission.Accepted`는 접수로 표시한다. 재사용되는 `GameplayFrame`에서 다음 프레임에 필요한 값만 복사해 보관한다. 기존 패킷, 저장 형식, `gamedata.json` 수치를 사용한다. [프로토콜](../protocol.md) · [게임 규칙](../Rules/gameplay.md)
- 안내·경보는 수신한 자기 상태와 현재 시야를 사용한다. 공간 표시는 `SpatialReady`, 현재 Fog와 카메라 화면을 함께 확인한다. 기억된 시설, 잔고 차이, 사라진 개체, 로컬 이동 예측을 성공 근거로 사용하지 않는다.
- 상단의 성장 단계 문장은 작업 1–2에서 장면별 표식으로 교체한다. 기존 후보·건물 타이머는 각자의 표시 영역에 둔다. 전장 학습 문구는 선택 분대와 관련 대상 하나씩을 상한으로 한다. 경보와 현재 실패를 우선하고, 겹치는 문구는 관련 HUD에 표시한다. 시안의 짧은 문구보다 기획서의 사용 조건을 우선한다.
- `조작 안내`는 학습 문구와 건설 버튼의 첫 강조에 적용한다. 입력 응답·획득·거절·위험·키 표기는 계속 읽을 수 있어야 한다. `ClientFlow.ApplyPreferences`에서 이미 전달하는 `UI 동작 줄이기`를 HUD와 전장 효과에도 적용해 점멸과 확대를 정적 윤곽으로 바꾼다. 키 이름은 `ControlBindings`에서 가져오고, 전장 표식은 `raycastTarget=false`로 설정한다. [Unity 설정 규칙](../Rules/unity.md)
- 기존 `ObservatoryUi`, `LegionUiArt`, 전장 타이머의 투영 방식과 미니맵 점을 재사용한다. 입력과 권위 관측의 연결은 `ClientBootstrap`, 표시용 상태는 해당 HUD partial에 둔다. 라벨과 렌더 버퍼를 재사용하고, 시야 대상 탐색은 새 관측이 들어왔을 때 수행한다. [프레젠테이션 규칙](../Rules/Patterns/presentation.md) · [기존 UI·이동 표시 재사용 조사](../Reports/2026-11-11-ui-controls.md)

**표시 시험값** 응답 대기 또는 움직임 정체가 1초를 넘으면 확인 중·지연으로 바꾼다. 미확정 요청 표시의 첫 상한은 3초, 짧은 핵 획득 표시는 1.2초다. 도착·귀환 고리는 기존 이동 표식 시간(기본 0.55초)과 귀환 도착 시간(기본 0.8초)을 활용한다. 새 시간·여백·글자 크기는 Inspector에서 조절하고 초행 시험으로 보정한다. 시전 시간·공유 대기·수량·비용은 기존 게임 데이터와 관측값으로 표시한다.

## 작업 0 · 현재 작업 트리에서 출발점 확인

- [ ] `git status --short --branch`와 `git diff --stat`를 기록한다. 작성 시점은 `codex/m0-simulation`의 `9831591`이며 코드·프리팹·테스트에 진행 중인 변경이 있다. 특히 지점 이동 고리와 미니맵 점선, 귀환 완료 고리는 현행 코드에 있는 기능이므로 해당 파일을 읽고 이어서 수정한다.
- [ ] 실제 `client/` 프로젝트의 `Application.dataPath`와 Unity 버전을 확인한다. 아래 EditMode 명령과 기존 `UiPreviewRenderer.CaptureUiControls(page, width, height, percent)`로 `Hud`, `Forces`, `RecallCasting`의 결과를 확보한다. 기존 실패가 있으면 이름과 증상을 따로 기록해 이번 변경과 비교한다.
- [ ] 기본 HUD와 창 하나를 연 HUD를 1920×1080·100%, 1280×720·110%에서 찍는다. 비교할 카메라·시드·병력 수·안개 상태를 기록한다.

## 작업 1 · 첫 이동 안내와 서버 목적지

**파일** `Runtime/Bootstrap/ClientBootstrap.cs`의 `TickMovement`, `OnCommandResponse`, `OnGameplay`, `SelectSquad`와 연결 초기화. `Runtime/UI/CombatHud.{Guidance,Squads}.cs`, `Runtime/Legion/ElementalVisuals.MoveDestination.cs`, `Runtime/UI/ObservatoryMap.cs`.

- [ ] 기존 `EditMode/MoveDestinationMarkerTests.cs`와 `SquadControlTests.cs`를 확장한다. 같은 순번 전의 관측·접수 응답에서는 점선, 같은 순번의 관측에서는 서버 좌표의 실선이 보인다. 수비대 보정, 늦은 이전 순번, 이동 지연, 정지, 드래그 해제와 새 선택을 각각 확인한다.
- [ ] 전장·미니맵의 유효한 우클릭을 기존 `SquadControlState.TryTakePoint` 전송 지점에서 한 번 기록한다. 전송 순번·대상 마스크·요청 좌표·시각과 대상별 `MovementRevision`·확정 좌표를 표시 상태가 갖는다. 권장 진입점은 HUD의 `TrackMoveRequest(ulong sequence, byte mask, System.Numerics.Vector2 point, double now)`다. 기존 로컬 고리와 좌표 안내는 이 상태에 연결한다. 배치 취소·UI 위 클릭·미니맵 좌클릭은 이 경로에 진입하지 않는다.
- [ ] `SquadView.AppliedSequence`가 기록과 일치하면 `Destination`의 실선 고리를 그린다. `Order`와 실제 중심 이동을 읽어 `이동 중`, 정체 시 `이동 지연`을 표시한다. 같은 이동의 `Hold`와 목적지 도달을 확인하면 `도착`을 짧게 띄운다. 첫 반영 관측이 이미 `Hold`이면 동일 순번과 `MovementRevision`에서 서버 중심과 목적지가 일치할 때만 도착을 확인한다. 새 정지·이동, 귀환 재배치, 더 높은 순번은 이전 도착 표시를 끝낸다. 확인된 거절은 해당 순번에만 연결한다.
- [ ] 확인 지연은 `이동 확인 중`, 만료는 `이동 응답을 확인하지 못했습니다`를 짧게 보여 준다. 드래그 중에는 최신 발행 목적지 하나만 남기고, 키·마우스를 떼어도 서버가 유지하는 이동 표식은 남긴다. 5번은 1–3의 전체 선택으로 처리하고, 대상 분대 중 하나만 도착한 경우 전체 도착을 표시하지 않는다. 방향키 이동은 점선·실선 목적지 없이 동작한다.
- [ ] 합류 병력이 있는 단일 야전 분대의 전장·미니맵·하단 번호를 맞춘다. 첫 이동 전 `바닥 우클릭 · 이동`, 빈 분대 `합류 0기 · 병력 창에서 지원`, 4번 `본진 안에서 이동`을 표시한다. 서버가 첫 유효한 이동을 반영하면 학습 문구를 접고, 하단 분대와 지도 호버에서 입력을 확인할 수 있게 한다. 새 입장에서는 첫 안내를 초기화한다. 재접속·승계에서는 옛 순번과 일회성 표식을 걷고 새 관측으로 다시 그린다.

## 작업 2 · 핵 획득과 중립 반응

**파일** `Runtime/Bootstrap/ClientBootstrap.cs`의 `OnGameplay`·`OnFogChanged`·`OnEntitiesForgotten`, `Runtime/UI/CombatHud.Guidance.cs`·`CombatHud.Layout.cs`, `Runtime/UI/ElementTooltip.cs` 및 기존 전장 표시.

- [ ] `EditMode/MainWorldHudTests.cs`에 동일 획득 사건 두 번, 타인 획득, 잔고만 증가, 시야 밖·화면 밖, 빈 분대·수비대, 중립 소유권 전환을 추가한다. 잔고는 관측대로 바뀌고, `+N`은 새 본인 획득 사건에서만 한 번 생긴다.
- [ ] 현재 `GameplayFrame.Cores` 중 선택한 유효 야전 분대에 가까운 현재 시야·화면 안의 핵 하나를 표시한다. Fog 상태를 `ClientConnection.Fog`의 `GetState`로 확인하고 잊힌 개체·시야 이탈 때 즉시 안내를 걷는다. 안내 대상이 없으면 이동 문구만 보여 준다. 같은 자리에서 중립을 가리킬 때는 기존 호버 판정을 재사용하고 `중립 · 사거리 안 자동 교전`을 보여 준다.
- [ ] `NetworkEvent.Serial`로 같은 연결에서 중복 표시를 막는다. `CorePickedUp`이고 `OwnerId == frame.PlayerId`인 사건의 `Amount`를 왼쪽 위 잔고 옆에 보여 준다. 현재 보이는 획득 위치에만 같은 아이콘과 수량을 추가한다. 이미 적용된 시야나 승계 전 사건을 재생하지 않도록 연결·호스트 기준 변경 시 표시 상태를 정리한다. `CoreBalance`는 프레임의 값을 그대로 그린다.
- [ ] 후보 확보는 기존 후보 타이머, 전향은 실제 소유권 전환, 합류는 `SquadView.JoinedCount`로 표현한다. 핵 드롭은 관측된 핵으로, 획득은 위 사건으로 표시한다. 획득 사건과 잔고 확인을 거친 뒤 첫 시제품을 캡처한다.

## 작업 3 · 건설 선택과 배치 결과

**파일** `Runtime/UI/ConstructionPanel.cs`의 `RefreshControls`, `RefreshDetail`, `PlaceAtPointer`. `Runtime/UI/CombatHud.Panels.cs`와 `ClientBootstrap.Build`·`OnGameplay`의 건설 분기.

- [ ] `EditMode/MainWorldHudTests.cs`와 `UiReconstructionTests.cs`에서 비용 부족·잠금·가용 시설, 배치 요청·실행 거절·착공·완공을 서로 다른 표시로 검증한다. 배치 중 우클릭은 기존 취소 경로를 지나며 지점 이동을 보내지 않는다.
- [ ] 기존 시설 카드에 포집기의 `핵 수급`, 응집로의 `병력 양성`, 수호 위성의 `본진 방어`를 붙인다. 비용·제한은 `GameRules`와 현재 관측으로 읽는다. 처음 사용 가능한 시설이 생기면 기존 `건설` 버튼을 한 번 강조한다. 선택지는 플레이어가 고른다.
- [ ] 기존 미리보기와 검증을 사용한다. 현지 검사를 통과한 반투명 모델에는 `배치 가능`, 실행을 보내면 `배치 요청`, 같은 순번의 `CommandApplied(Build)` 또는 착공 관측에는 `건설 중`을 표시한다. 이 실행 사건에는 시설 ID가 없으므로 보낸 시설 종류와 그리드 위치를 기억하고 새 소유 시설 관측에서 ID를 연결한다. 그 시설의 `IsUnderConstruction`이 끝날 때 완공을 표시한다. 사라짐·실패·패널 닫기·다른 명령은 서로의 결과로 쓰지 않는다.

## 작업 4 · 양성 비용과 병력 도착

**파일** `Runtime/UI/CombatHud.Training.cs`의 `RefreshTrainingDetails`·`RefreshTraining`, `CombatHud.cs`의 `UpdateTrainingQueue`, `ClientBootstrap.OnGameplay`의 `Trained` 처리.

- [ ] `EditMode/TrainingBatchHudTests.cs`에서 주력이 물일 때 `물 1기 · 8핵 → 2번`, 4번 목적의 `수비대` 문구와 총비용을 확인한다. 예약·진행·완료는 별도 상태이며 취소는 주문 ID를 유지한다. 창을 닫아도 주문과 목적 설정이 유지되고, 양성 후 수비대와 야전 목적의 결과가 구분된다.
- [ ] 기존 확정 버튼에 원소·수량·총비용·목적을 한 줄로 표시한다. `TrainingQueue`는 예약, `TrainingElement`·`TrainingTargetSquad`·시작/종료 틱은 진행 바에 연결한다. 양성 완료는 자기 `Trained` 사건으로 알리고, 사건으로 목적을 확인할 수 없으면 완료 문구에 목적 번호를 붙이지 않는다. 1–3번의 이동 중 증원·합류와 4번의 수비대 인원은 해당 분대 관측으로 표시한다.

## 작업 5 · 병력 창과 빠른 전송

**파일** `Runtime/UI/CombatHud.Forces.cs`, `CombatHud.Squads.cs`, `CombatHud.Panels.cs`, `CombatHud.TryGetRally`·`ShowRally`, `ClientBootstrap.Rally`·빠른 전송 응답. 미니맵은 `ObservatoryMap`의 기존 번호·점에 연결한다.

- [ ] `EditMode/SquadBindingTests.cs`·`SquadShortcutTests.cs`·`ObservatoryMapTests.cs`에 G 클릭과 키가 같은 대상·수량을 보내는지, 출발 2/1/0기, 수비대 목적 상한, 이동 중 손실, 선택 보존을 확인한다. 720p·110%에서 버튼 문구와 `재편`이 겹치지 않는다.
- [ ] 병력 창의 분대 목록 위에 `4 수비대 → 2번 · 1기 보내기 [G]` 형태의 기존 집결 버튼을 배치한다. 목적 1–3은 그 자리의 버튼으로 고르고, 수량 기본 1과 상세 입력은 기존 `재편`을 사용한다. 버튼과 G는 `TryGetRally(out byte target, out int count)`의 같은 값으로 지정 수량을 전송한다. 부족·잘못된 수량을 해당 줄에 표시하고 숫자 입력 중 분대 키는 기존대로 차단한다.
- [ ] 숫자 조합 안내는 현재 키와 `rules.QuickTransferCount`를 읽는다. 실제 출발은 자기 `CommandApplied(QuickTransfer)`의 순번·수량과 보낸 출발/목적을 대조해 `4 → 2 · 2기 출발`로 표시한다. 키 조합을 쓰는 동안과 처리 뒤에도 선택·카메라를 유지한다. G는 접수 표시와 실행 거절의 연결을 구분해, 접수 뒤 거절도 해당 버튼 옆에 표시한다. 실제 이동 중·합류 인원은 `ReinforcingCount`와 `JoinedCount`로 읽는다. 미니맵의 이동 점은 관측 유닛의 움직임을 사용한다.

## 작업 6 · 본진 경보와 양자 이동

**파일** `Runtime/Bootstrap/ClientBootstrap.cs`의 `FocusBase`·`Recall`·`OnGameplay`, `Runtime/UI/CombatHud.cs`·`CombatHud.Layout.cs`·`CombatHud.Squads.cs`, `ObservatoryMap.cs`, `Runtime/Legion/ElementalVisuals.cs`의 기존 귀환 표현.

- [ ] `EditMode/DefenseHudTests.cs`·`RecallHudTests.cs`·`SquadPresentationTests.cs`를 확장한다. 본진 체력이 그대로여도 내 시설 피격 사건이면 경보가 보인다. B 뒤 선택·기존 이동 명령이 유지된다. 시전 중 이동·피격·출발 분대 전송, 자리 부족, 수신 누락·승계, 정상 귀환에서 경보·귀환 상태가 구분된다.
- [ ] 내 `BaseUnderAttack` 사건으로 체력 영역과 미니맵의 본진에 같은 `!`를 표시한다. 기존 B 버튼을 `본진 보기 [현재 키]`로 표기해 한 곳에서 실행한다. 관리 창을 열어도 경보를 읽을 수 있다. 피격 시설을 화면에 표시할 때는 사건의 피해 위치와 현재 시야만 사용한다. 반복 피격은 기존 경보 표시를 갱신하고, 종료 시 기본 상태로 돌린다.
- [ ] R은 기존 `TryGetRecall`과 `DiscardPendingPoint`를 통과하는 단일 야전 분대의 입력으로 연결한다. 보낸 순번에 `귀환 요청`을 붙이고 응답을 기다리는 동안 같은 요청을 중복 전송하지 않는다. 관측 `RecallSquad`와 `RecallEndTick`으로 시전과 진행을 표시한다. 4·5번, 빈 야전, 공유 대기 상태는 버튼 옆에서 사용 조건을 읽게 한다.
- [ ] `ElementalVisuals.Observe`에서 `IsRecallSuccess`로 확인한 분대 번호를 같은 `OnGameplay`의 HUD 갱신에 전달한다. 시전 종료 틱 이후 분대 시전이 해제되고 새로운 `RecallReadyTick`이 확인된 경우에 완료를 표시한다. 기존 시전·도착 렌더를 같은 양자 고리로 맞추고, 도착한 분대는 본진에서 같은 번호를 유지한다. 새 연결에서 공유 대기만 보인 경우 대기 시간을 표시한다. 같은 요청의 `CommandRejected`는 중단·실패 사유를 표시하고 요청 상태를 정리한다.

## 작업 7 · 캡처, 통합 검증, 인계

- [ ] 기존 `client/Assets/_Project/Scripts/Editor/UiPreviewRenderer.cs`의 프리팹·게임 관측 생성과 캡처 검사를 재사용한다. 작업 1–6의 요청·반영·수신 사건을 같은 UI 진입점에 전달하는 시나리오를 추가하고, `CaptureFirstPlayUxMatrix()`에서 7장과 두 해상도를 실행하도록 연결한다. Editor 메뉴 `Elemental Legion/UI/Capture First Play UX` 또는 배치의 `-executeMethod ElementalLegion.Client.Editor.UiPreviewRenderer.CaptureFirstPlayUxMatrix`로 실행한다. 결과는 `.artifacts/first-play-ux/`에 보관한다. 상태를 직접 주입한 컷과 실제 Play 화면에는 조건을 붙인다.
- [ ] 시안 7장에 대응하는 장면을 1920×1080·100%와 1280×720·110%에서 캡처한다. 실제 폰트로 글리프 누락과 조작 영역의 화면 밖 이탈이 0건이어야 한다. HUD·전장 표식·기존 후보 타이머가 겹치는지 확대해 보고, 동작 줄이기와 무음에서도 번호·모양·짧은 상태로 읽는지 확인한다.
- [ ] 실제 로컬 게임에서 미니맵 좌·우클릭, 배치 취소, 숫자키 눌렀다 떼기, 전송, B·R, 키 재설정, 채팅·ESC·포커스 전환을 순서대로 실행한다. 밀집 화면은 같은 시드·카메라·해상도·병력 수로 전후 프레임 시간과 GC 할당을 비교한다. 새 표식은 변경 없는 프레임마다 문자열·배열·UI 오브젝트를 생성하지 않아야 한다.
- [ ] 완료한 체크박스를 갱신하고 증거는 `Docs/Reports/2026-11-11-first-play-ux.md`에 연결한다. Editor 컴파일, EditMode, 캡처, 실제 Play, 초행자 시험은 실행한 조건과 결과를 각각 기록한다.

## 검증 명령과 통과 기준

Unity Hub에서 `client/`를 연 Editor가 있으면 해당 프로젝트의 Test Runner에서 `ElementalLegion.Client.EditModeTests`를 실행한다. Editor가 닫힌 환경에서 배치 실행할 때는 저장소 루트에서 아래 명령을 사용한다. 연결된 그래픽 장치가 있는 환경에서 화면 검사를 한다.

~~~powershell
$uxUnityExe = 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe'
$uxProjectPath = (Resolve-Path -LiteralPath 'client').Path
$uxResultDir = (New-Item -ItemType Directory -Path '.artifacts/first-play-ux' -Force).FullName
$uxTest = Start-Process -FilePath $uxUnityExe -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    '-batchmode', '-projectPath', ('"{0}"' -f $uxProjectPath),
    '-runTests', '-testPlatform', 'EditMode', '-assemblyNames', 'ElementalLegion.Client.EditModeTests',
    '-testResults', ('"{0}\editmode.xml"' -f $uxResultDir), '-logFile', ('"{0}\editmode.log"' -f $uxResultDir)
)
if ($uxTest.ExitCode -ne 0) { throw "Unity EditMode exit $($uxTest.ExitCode)" }
~~~

종료 코드와 XML의 실제 실행 수·실패 수, Editor 로그의 컴파일 오류를 확인한다. 서버·공유 파일에 영향이 생긴 경우에만 [프로젝트의 .NET 빌드·테스트](../Rules/workflow.md#명령어)를 추가한다. Unity 테스트나 Editor 캡처가 통과한 상태에서도 실제 입력·가독성은 아래 플레이 장면에서 확인한다.

| 실제 플레이 시나리오 | 통과 조건 |
| --- | --- |
| 이동 지연·연속 드래그·건설 취소·승계 | 오래된 목적지나 잘못된 도착이 남지 않는다. 서버 보정 위치와 미니맵 목적지가 일치한다. |
| 내 핵, 타인이 선점한 핵, 시야에서 사라진 핵 | 확인된 내 획득에만 같은 아이콘과 수량이 뜬다. 빈 분대나 수비대에 야전 획득 문구가 붙지 않는다. |
| 시설 비용 부족·착공·완공, 4번과 2번 양성 | 비용·잠금과 요청·완공을 구분하고, 수비대 인원 또는 이동 중 증원·합류의 실제 결과를 읽는다. |
| 1번 선택 중 4를 누른 채 2, 이어서 G 지원 | 실제 출발 수와 증원이 일치한다. 출발·목적·수량이 화면에 드러나고, 빠른 전송 뒤 1번 선택과 카메라가 유지된다. |
| 원정 중 본진 경보, B로 보기, R 성공·중단 | 공격 위치를 추측하는 표식이 없다. B 뒤 선택이 유지된다. 실제 도착 시에만 완료 고리와 새 공유 대기가 생긴다. |
| 720p·UI 110%, 무음·동작 줄이기, 키 재설정 | 전장 표식이 클릭을 가로채지 않는다. 위치·상태·경보·현재 키를 읽고 관리 창을 닫을 수 있다. |

**초행 시험값** 현행과 개편 화면에 초행자 6명씩을 같은 조건에서 배정한다. 설명 없이 시작해 개편 화면의 6명 중 5명 이상이 60초 안에 분대를 움직이고 120초 안에 자신의 첫 핵 획득을 알아보는지 본다. 이동까지 걸린 시간과 요청을 결과로 오해한 횟수도 기록한다. 인원을 확보해 실제 플레이했을 때의 평가 기준이다.

## 다른 채팅에 전달할 문장

> `Docs/Plans/2026-11-11-first-play-ux.md`를 열고 연결된 첫 플레이 UX 기획서와 시안 7장을 읽어 주세요. 작업 0으로 현재 트리와 검증 기준을 확인한 뒤 작업 1–2를 먼저 구현하고, 시제품 결과를 확인한 후 작업 3–7을 진행해 주세요. 기존 변경을 보존하고 시안의 수량·시간은 해당 서버 관측과 게임 데이터로 표시해 주세요. 각 작업에 실제 실행한 검증과 결과를 남겨 주세요.
