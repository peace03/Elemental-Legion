# UI와 분대 단축키 개편 구현 계획

> **에이전트 실행 지침** 구현 요청을 받으면 `superpowers:subagent-driven-development` 또는 `superpowers:executing-plans`를 적용해 작업별로 진행한다. 체크박스는 구현·검증 진행을 기록한다. 다른 채팅에서 이 문서와 시안 인덱스를 먼저 읽는다.

**Goal** 전장을 넓게 볼 수 있는 HUD를 만들고, 숫자키를 떼어 분대를 선택하거나 두 분대 키를 조합해 최대 3기를 보내는 조작을 제공한다.

**Architecture** 기존 uGUI·TMP, `ClientFlow`의 화면 전환, `ClientBootstrap`의 명령 전송을 재사용한다. 키 조합의 진행 상태와 HUD에서 연 창은 클라이언트가 관리한다. 병력 이동·소유권·합류·수비대 슬롯과 성공 수량은 서버가 판정한다.

**Tech Stack** Unity 6000.6.0f1, 기존 Input System·uGUI·TMP, .NET 10, 공유 C# 9 / netstandard2.1, 기존 UDP/WSS와 NUnit·xUnit.

**Spec** 이 문서의 대화 결정과 [메인 게임 개선안](../기획서/2026-11-06-main-world-improvement.md#조합키-안내와-미니맵)을 함께 적용한다. 화면 배치의 참조는 [UI 시안 인덱스](../Art/Concepts/2026-11-11-ui-index.md)다. 앞선 [메인 게임 구현 계획](2026-11-09-main-world-improvement.md)의 전투·경제·저장 작업과 연결한다.

2026-11-11 작성. 계획을 작성할 때 조사한 작업 트리는 `codex/m0-simulation`, HEAD `9831591dc0d62e586ef55351e387e7b35e46b80b`와 미커밋 변경분이다.

## 구현·검증 기록

서버의 빠른 전송과 저장 이관, 단축키, HUD·관리 창·미니맵·소개 카드, 현황·ESC·설정을 구현했다. .NET 829/829, Unity EditMode 207/207과 Editor 캡처 150장을 확인했다. 아래 미완료 항목은 실제 Play의 입력·전환과 Player에서 확인할 범위다. 미니맵 메시의 Editor 측정과 실플레이에서 남은 항목은 보고서에 기록했다. [구현 보고서](../Reports/2026-11-11-ui-controls.md)

## 인계할 결과와 범위

- 기본 HUD, 양성·병력·건설 창, 현황판, ESC 메뉴, 설정, 건물·원소 소개를 연결한다.
- 숫자키 선택·조합, B·R·G·Tab·ESC, 마우스 이동·건설·미니맵, 채팅과 키 재설정의 우선순위를 맞춘다.
- 원거리 분대 사이 빠른 전송을 서버의 기존 증원 이동으로 연결한다. 이동 도중 전투·사망과 도착 후 합류를 처리한다.
- 게임의 전투 수치·경제·인구·승패는 기존 데이터에서 읽는다. 근원 쟁탈전의 제한 시간·관문·재점화는 이 작업의 대상에 포함하지 않는다.
- PNG는 배치·색·정보량을 검토하는 참조다. 버튼·텍스트·지도는 실제 UI 요소로 구성한다. 이미지 속 예시 이름·점수·핵·인구·음량을 실행 값으로 쓰지 않는다.

## 공통 제약

- 서버가 인증된 소유자의 명령을 판정한다. 다른 플레이어의 숨은 위치·원소·시설·자원을 화면이나 실패 사유로 공개하지 않는다.
- 게임 규칙의 원본은 `shared/Data/gamedata.json`이다. 빠른 전송의 수량 3을 서버와 클라이언트가 읽는 새 `legion.squads.quickTransferCount`로 둔다. 기존 밸런스 항목의 값은 유지한다.
- 공유 코드는 C# 9 / netstandard2.1과 Unity 비의존성을 지킨다. 이동·합류에 Unity 물리를 사용하지 않는다.
- 기존 18개 키 설정과 구형 10·11개 키 저장값의 복원 경로를 이어 쓴다. 키 조합은 설정된 분대 키로 동작한다.
- 패널 여백·지도 번호 크기·이동 판정 문턱·점의 주기·피드백 유지 시간은 한글 툴팁이 있는 Inspector 값으로 조절한다.
- 그림·아이콘은 기존 `LegionUiArt`와 프로젝트 자산을 재사용한다. StarCraft의 부대 선택과 LoL의 집중된 상태·스킬 배치는 참고 방향이며 외부 게임의 아트·로고를 가져오지 않는다.
- 기존 설정 저장·화면 적용 확인·키 중복 검사, 예약 취소·집결·건설 해금·업그레이드·상성표·채팅·재접속을 각 작업의 회귀 범위로 삼는다.
- 작업마다 관련 테스트와 Editor 검증을 수행한다. 실제 Play·최종 화면·Player 빌드는 [프로젝트 검증 기준](../Rules/workflow.md#작업별-검증)에 따라 실행 여부와 결과를 기록한다.

## 대화에서 정한 방향

| 항목 | 구현할 동작 |
| --- | --- |
| 분대 선택 | `1`~`4`를 단독으로 누르고 떼면 해당 분대를 선택하고 카메라도 이동한다. |
| 전체 선택 | `5`를 단독으로 누르고 떼면 야전 1~3을 선택한다. 수비대 4는 별도로 제어한다. |
| 병력 보내기 | 출발 분대 키를 누른 채 다른 분대 키를 누르면 출발 분대에서 최대 3기를 목적 분대로 보낸다. 출발 병력이 2기나 1기면 가능한 수만 보낸다. |
| 조합 안내 | 첫 키를 누르는 동안 출발 분대를 강조하고, 목적 키 입력 뒤 방향·요청 수량·서버 결과를 잠깐 표시한다. |
| 미니맵 | 내 분대 1~4의 위치를 번호로 표시한다. 이동 중인 원소는 천천히 밝기가 바뀌는 점으로 표시한다. |
| HUD | 상단의 자원·본진 상태, 왼쪽 아래 미니맵, 아래쪽 분대·양자 이동, 오른쪽 아래 관리 버튼으로 모은다. |
| 관리 창 | 양성·병력·건설은 같은 오른쪽 영역에서 하나씩 연다. 복잡한 관리 작업은 해당 창 안에서 진행한다. |
| 귀환 | `R`은 선택한 야전 분대의 **양자 이동**이다. 원시핵과의 연결을 맞춰 본진으로 귀환한다. 시전·취소·공유 대기는 서버 상태로 표시한다. |
| 정보·메뉴 | 현황판·ESC 메뉴·설정은 중앙 창, 건물·원소 소개는 오른쪽 정보 카드로 구성한다. |

### 계획에서 구체화한 구현 기본안

아래는 대화의 방향을 코드로 옮기기 위해 정한 기본안이다. 사용자에게 별도 승인을 받은 항목으로 표기하지 않는다.

| 세부 사항 | 기본안과 이유 |
| --- | --- |
| 조합 범위 | 출발·목적은 실제 분대 1~4로 한정한다. 5는 야전 전체 선택으로 사용한다. |
| 선택 유지 | 조합 전송은 현재 선택과 카메라 추적 모드를 바꾸지 않는다. 1번 조종 중 4→2를 보내도 1번을 계속 조종한다. |
| 이동과 소속 | 빠른 전송은 원소의 현재 위치에서 목적 분대로 걸어가게 한다. 이동 중에는 `SquadId=0`, `DestinationSquadId=목적`, `IsReinforcing=true`로 두고 도착 때 정식 합류시킨다. 기존 양성 증원의 상태와 이동을 재사용한다. |
| 기존 재편과의 계약 | 기존 `Transfer`의 지정 수량·본진 근처 재편과 기존 `Rally` 호출은 이어 쓴다. 빠른 전송에는 별도 `QuickTransfer` 명령을 추가해 원거리 이동과 부족 수량 처리를 명확하게 한다. |
| 수비대 여유 부족 | 출발 병력이 부족하면 가능한 수를 보낸다. 목적이 4번이고 그 수를 받을 슬롯이 부족하면 전체 요청을 거절하고 수비대 한도를 알린다. 임의로 병력을 삭제하거나 슬롯을 초과하지 않는다. |
| 빈 분대 | 합류한 생존 원소만 보낼 수 있다. 이동 중인 증원·양성 예약·전향 후보는 출발 수량에서 제외한다. 빈 목적 분대는 기존 중심과 합류 경로를 이용한다. |
| 현황 진입 | 기본은 Tab 누르기다. 자원 캡슐에 작은 현황 버튼을 두어 마우스로도 열 수 있게 한다. 마우스로 연 현황은 닫기·ESC로 닫는다. |
| 소개 카드 | 관리 창을 닫은 상태에서 전장의 건물·원소에 마우스를 올리면 오른쪽 카드로 미리 본다. 대상을 좌클릭하면 카드를 열어 두고 ×·ESC로 닫는다. 관리 창의 아이콘 호버는 그 창 안의 설명만 갱신한다. 배치 중에는 배치 안내가 우선이다. |
| 시안에서 생략된 상태 | 대기열·잠금 이유·업그레이드·타워 원소 선택·설정의 다른 탭·G 집결은 아래 작업에서 실제 상태와 함께 구성한다. |

## 키와 포인터의 입력 계약

분대 ID는 1·2·3·4이고 선택 마스크는 각각 1·2·4·8이다. `5`는 마스크 7로 선택한다. ID 4와 마스크 4를 혼동하지 않는다.

| 입력 | 반응 |
| --- | --- |
| 1~4 key-down | 선택을 바꾸지 않고 출발 후보를 표시한다. |
| 같은 키 key-up | 조합에 쓰이지 않았으면 해당 분대 선택·카메라 이동을 한 번 확정한다. 한 키의 누름·해제가 같은 프레임에 들어온 경우도 단독 입력으로 처리한다. |
| 출발 키 held + 다른 1~4 key-down | 빠른 전송을 한 번 요청한다. 두 키의 key-up으로 선택이 바뀌지 않도록 소비한다. |
| 출발 키 held + 목적 키를 다시 떼었다 누르기 | 새 key-down마다 다음 요청을 한 번 보낸다. OS 반복 입력·계속 누르기는 추가 요청을 만들지 않는다. |
| 출발 키를 먼저 떼기 | 조합에 사용한 목적 키가 남아 있어도 선택을 확정하지 않는다. 키를 모두 뗀 뒤 새 동작을 시작한다. |
| 같은 키의 반복 신호 | 새 전송을 만들지 않는다. 다른 키가 없었으면 key-up에 단독 선택한다. |
| 5와 다른 분대 키의 조합 | 전송하지 않는다. 두 키를 모두 뗄 때까지 관련 key-up을 선택으로 해석하지 않는다. |
| 같은 프레임에 순서를 판별할 수 없는 복수 key-down·세 키 동시 누름 | 명령을 만들지 않고 모두 뗄 때까지 소비한다. 출발·목적을 번호순으로 임의 결정하지 않는다. |
| 하단 분대 버튼 좌클릭 | 같은 선택 진입점을 사용해 선택·카메라 이동을 확정한다. |
| WASD·방향키 | 현재 선택 부대를 움직인다. 다른 부대는 기존 명령을 이어 간다. |
| 전장·미니맵 우클릭/우드래그 | 선택 부대에 지점 이동을 지정한다. UI 위의 클릭은 전장 명령으로 전달하지 않는다. |
| 미니맵 좌클릭·좌드래그 | 카메라만 옮긴다. 분대 번호는 지도 입력을 가로채지 않는다. |
| B | 선택을 유지하고 본진으로 카메라를 옮긴다. |
| R | 단일 야전 분대의 양자 이동을 요청한다. 수비대·전체 선택·시전·대기 조건은 기존 서버 판정을 따른다. |
| G | 기존 병력 창의 집결 대상·수량으로 수비대를 출격시킨다. 입력 수량 기본 1기와 목적 기본 1번을 이어 쓰며 병력 창에서 조절한다. |
| Tab held / released | 현황판을 열고, 키를 떼면 닫는다. 화면 로딩 중 떼어도 뒤늦게 다시 열리지 않는다. |
| ESC | 키 재설정 입력 대기와 화면 적용 확인, 채팅 작성, 건설 배치, 열린 정보·관리 창, 현황·설정·메뉴 순으로 현재 문맥에 맞는 취소·닫기를 한 번 처리한다. |
| 포커스 이탈·채팅 작성·모달·재접속/승계 | 대기 중 조합과 우드래그를 초기화한다. 제어가 돌아와도 미해제 분대 키나 이전 우드래그로 선택·전송·지점 이동을 다시 시작하지 않는다. |

키 재설정에서는 `ControlBindings.Label`로 실제 키 이름을 표시한다. 설정에서 분대 키를 바꾸면 조합 안내도 같은 키를 사용한다. 일반 HUD를 클릭한 뒤에도 WASD 제어는 이어진다. 채팅·모달·키 재설정은 전장 입력을 차단한다. 현황·메뉴·설정을 열어도 온라인 월드와 서버 전투는 계속된다.

### 플레이 예시

1. 1번을 조종하면서 `4`를 누른다. 1번의 선택 테두리는 유지되고 출발 후보 4번을 따로 강조한다.
2. 4를 누른 채 `2`를 누르면 출발 4·목적 2의 빠른 전송을 보낸다. `4 → 2 · 3기 출격 요청`을 표시한다.
3. 2와 4를 떼어도 선택·카메라가 바뀌지 않는다. 서버가 2기를 보냈다면 안내를 `4 → 2 · 2기 출격`으로 갱신한다.
4. 미니맵에는 2번의 번호와 이동 중인 두 점을 표시한다. 병력 창은 2번의 합류 인원과 이동 중인 2기를 구분한다.
5. 이후 `3`을 단독으로 눌렀다 떼면 3번으로 선택·카메라가 이동한다.

## 시안과 화면별 완료 조건

[원본 9장과 생성 프롬프트를 보는 인덱스](../Art/Concepts/2026-11-11-ui-index.md). 아래 UI 번호는 해당 인덱스와 같다.

| 시안 | 구현 기준 |
| --- | --- |
| UI-01 기본 HUD | 자원·인구·예약, 본진 체력·경보와 B, 미니맵, 1~5·인원·R, 양성·병력·건설만 상시 표시한다. 상세 구성·드롭다운·수량 입력은 관리 창으로 옮긴다. |
| UI-02 양성 | 자연/에너지 탭, 원소, 수량 1~최대 묶음, 목적 1~4, 총비용·기당 시간·확정 버튼을 제공한다. 진행 항목과 대기 항목의 목적을 표시하고 대기 주문은 ID로 개별 취소한다. |
| UI-03 병력 | 1~4의 합류 인원·이동 중 증원·예약·명령 상태를 구분한다. 원소 구성 탭과 재편 접힘 영역에서 기존 지정 수량 재편·수비대 복귀·G 집결을 조작한다. |
| UI-04 건설 | 시설·핵 비용·해금/부족 이유, 건설 시간, 배치·취소를 표시한다. 자연/에너지 수호 위성과 원소 선택, 본진 업그레이드·최대 단계·자기권 고정 설치를 포함한다. |
| UI-05 현황 | 내 군단과 공개 총점을 표시한다. 여정 기록 탭에서 누적·마지막 확인 이후를 제공하고 상성표로 연결한다. Tab으로 열었을 때 떼면 닫는 동작을 내부 탭·상성표에도 적용한다. |
| UI-06 ESC | 계속하기·설정·종료하기, 전투 계속 안내를 표시한다. 종료 확인과 타이틀/프로그램 종료의 기존 경로를 연결한다. |
| UI-07 설정 | 음향·화면·표현·조작 키 탭을 유지한다. 음향은 즉시 저장, 화면·표현은 기존 적용·확인·되돌리기, 조작 키는 중복 검사·기본값 복원·입력 취소를 수행한다. |
| UI-08 건물 소개 | 시설 이름·기능·체력·범위·비용·시간·건설/가동 상태를 표시한다. 내 시설의 동적 수치는 관측값으로 읽는다. 다른 소유자의 공개 범위는 현재 시야 규칙을 따른다. |
| UI-09 원소 소개 | 이름·그룹·역할·기본 능력치·효과·시너지·양성 비용/시간을 데이터로 읽는다. 물의 8핵·6초는 물이 주력일 때의 예시다. 현재 체력을 제공할 수 없는 문맥에서는 최대 체력으로 표기한다. |

기본 화면에서 패널이 차지하는 면적 20% 이내를 첫 검토 목표로 삼는다. 해상도·UI 크기에서 필수 텍스트와 클릭 영역을 읽을 수 있는지 먼저 확인하고 목표 수치를 조정한다. 1920×1080을 기준으로 1280×720, 2560×1440과 기존 UI 90~110%를 검토한다.

## 현재 코드와 변경 대상

| 책임 | 현재 확인한 경로와 보완점 |
| --- | --- |
| 키·선택·카메라 | `client/Assets/_Project/Scripts/Runtime/Input/{ControlBindings,LegionInput}.cs`, `Bootstrap/ClientBootstrap.cs`, `Legion/{SquadControlState,SnapshotTimeline}.cs`, `Camera/LegionCamera.cs`. 현재 숫자키는 누름 때 선택한다. 기존 `Transfer(target,count)`는 출발을 현재 대표 분대에서 읽는다. |
| 서버 전송·합류 | `shared/Runtime/Sim/{SimCommand,SquadSystem,MovementSystem,SimulationWorld,MigrationWorldState,WorldCheckpoint}.cs`. 현재 야전 목적 Transfer는 본진 근처에서 재편한다. 빠른 전송의 원거리 이동을 별도 경로로 연결한다. |
| 패킷·관측 | `shared/Runtime/Net/{Snapshot,Protocol,ClientConnection,GameplayCodec,GameplayFrame,GameplayReplicationState}.cs`, `Docs/protocol.md`. 기존 Transfer는 출발·목적·수량을 가진다. 빠른 전송의 의미와 실행 결과를 추가한다. |
| 저장 변환 | `server/Server/{WorldSaveMigration,FileWorldStore}.cs`, `shared/Runtime/Sim/WorldCheckpoint.cs`. 현재 파일 래퍼는 Format 2, 월드 스키마는 3이다. 기존 명시 변환기는 Format 1을 읽으므로 현재 형식은 별도 검증 분기가 필요하다. |
| HUD·관리 창 | `client/Assets/_Project/Scripts/Runtime/UI/CombatHud{,.Layout,.Squads,.Guidance}.cs`, `ConstructionPanel.cs`. 현재 양성 창은 처음부터 열리고 재편·집결 수량 입력이 상시 노출된다. |
| 미니맵 | `UI/ObservatoryMap.cs`. 현재 안개·관측 개체·수비대와 집결 경로를 그린다. 분대 번호와 이동 점, 선택 상태를 추가한다. |
| 현황·메뉴·설정 | `Bootstrap/ClientFlow.cs`, `UI/{ClientScreen,ClientSettingsScreen,ClientPreferences}.cs`. Tab/ESC·입력 차단·화면 적용 복원을 재사용한다. |
| 소개·그림 | `UI/{BuildingTooltip,LobbyElementHover,LegionUiArt,ObservatoryUi,RuntimeUiControls}.cs`, `Legion/ElementalVisuals.cs`. 건물 호버는 기존 `TryHoverBuilding`을 재사용한다. 원소 설명은 데이터와 수신 개체를 연결한다. |
| 연결·생성 | `client/Assets/_Project/Prefabs/LegionClient.prefab`, `Scripts/Editor/{ClientSceneBuilder,UiArtSetup,UiPreviewRenderer}.cs`, `Resources/UI/LegionUiArt.asset`, `Settings/UI/LegionUiSDF.asset`. Inspector·GUID·생성 경로와 한글 글리프를 함께 확인한다. |

경로가 여러 개 묶인 표에서는 중괄호 안 파일을 각각 확인한다. 실제 파일 분리는 책임과 현재 크기를 보고 정하되, 아래에서 이름을 정한 새 파일은 그 책임으로 생성한다.

## 검토에서 놓치기 쉬운 입력

1. 조합에 사용한 키를 떼거나 채팅·모달에서 돌아올 때 선택·카메라·명령이 새로 발생하지 않아야 한다. 작업 2에서 고정한다.
2. 출발 1~2기와 명령 직전의 사망·수비대 슬롯 예약이 경합해도 인구가 복제되거나 사라지지 않아야 한다. 작업 1에서 고정한다.
3. 관리 창·건설 배치·소개 카드 위의 클릭과 ESC가 전장 이동으로 전달되지 않아야 한다. 작업 3·6·8에서 고정한다.
4. 재접속·승계·시야 이탈 뒤 번호와 이동 점·상대 정보가 남지 않아야 한다. 작업 7·9에서 고정한다.
5. 720p·110%와 긴 한글·키 재설정 상태에서도 비용·수량·취소·닫기와 경보를 읽을 수 있어야 한다. 작업 10에서 고정한다.

## 작업 순서와 의존성

작업 1 → 2로 실제 키 조합 전송을 먼저 연결한다. 작업 3을 공통 바탕으로 4~6·8을 진행한다. 작업 7은 2·3의 선택 상태를, 작업 9는 3의 창 상태를 사용한다. 마지막으로 10에서 화면·회귀를 확인한다. 독립 작업을 위임할 때는 [역할 규칙](../Rules/subagents.md)에 따라 파일 소유 범위를 지정한다.

### 작업 1 · 서버의 빠른 전송과 실제 수량

**파일** 수정 `shared/Data/gamedata.json`, `shared/Runtime/Data/GameRules.cs`, 위 표의 서버 전송·패킷·저장 변환 경로와 `Docs/protocol.md`. 테스트는 `server/Tests/{GameRulesTests,SquadCommandTests,SquadProtocolTests,SquadMigrationTests,ReinforcementTests,GuardReturnTests,WorldSaveMigrationTests}.cs`를 재사용한다.

**계약** 새 `GameRules.QuickTransferCount`는 JSON의 3을 읽는다. 새 `SimCommand.QuickTransfer(ulong sequence, byte from, byte to)`, `ClientConnection.SendQuickTransfer(ulong sequence, byte from, byte to)`, `Protocol.WriteQuickTransfer(NetDataWriter writer, ulong sequence, byte from, byte to)`를 연결한다. 요청은 출발·목적만 전달하고 서버가 수량을 정한다.

- [x] `QuickTransferUsesOnlyLivingJoinedMembers`에 출발 합류 인원 0/1/2/3/5의 실제 이동 수 0/1/2/3/3을 고정한다. 0이면 실행 실패다. 다른 소유자·동일 분대·5번·중복 순번·죽은 원소·이동 중 증원은 요청이나 출발 대상에서 제외한다.
- [x] `QuickTransfer` 명령·패킷을 추가한다. 패킷 본문은 기존 명령의 양의 순번과 출발·목적 ID를 사용한다. 구현 시점의 미사용 식별자를 배정하고 현재 버전 22를 기준으로 프로토콜 버전을 갱신한다. `Protocol.TryReadCommand`, `SimulationWorld.TryEnqueue`와 실행 분기에 연결하고, 잘못된 길이·순번·ID는 거절한다.
- [x] `SquadSystem.Apply`에서 현재 합류 원소를 안정적인 ID 순서로 고른다. `min(QuickTransferCount, available)`만큼의 원소를 원래 위치에서 기존 증원 이동으로 바꾼다. 잔류 병력의 명령·분대 중심을 유지하고 목적 4의 여유가 부족하면 변경 전에 전체 거절한다.
- [x] 출발 분대가 양자 이동을 시전 중이면 실제 출발이 확정될 때만 기존 취소 경로로 시전을 중단한다. 일부·전원 출격과 수량·슬롯 거절을 검증한다. 목적 분대의 시전은 이어지고 합류 전 증원은 귀환 대상에 포함되지 않는다.
- [x] 성공한 출발 수량을 본인 `CommandApplied`의 `Amount`, 새 명령 종류를 `Detail`, 원래 순번을 `Sequence`에 기록한다. `GameplayCodec`의 허용 detail을 갱신하고 수량이 1~`QuickTransferCount`의 정수인지 검사한다. 잘못된 수량·다른 소유자 이벤트의 거부와 본인 왕복을 테스트한다. `Accepted`는 접수 단계로 표시하고 실제 출발은 실행 사건에서 확정한다.
- [x] 원거리 1→2, 4→2, 2→4, 빈 목적, 이동 중 피격·사망, 목적 이동·양자 이동, 원시핵 파괴, 재접속·승계를 검증한다. 체력·상태·ID·총인구와 예약을 보존하며 도착 전 시너지에는 합류시키지 않는다.
- [x] 기존 Transfer·Rally·양성 증원의 테스트와 전향/구출 출처를 회귀 검증한다. `MigrationWorldState`의 호스트 승계용 대기 명령 변환·범위 검사에 새 명령을 연결한다. 디스크의 증원 상태는 기존 필드로 왕복을 검사한다.
- [x] JSON 추가로 데이터 해시가 달라지는 현재 Format 2 / Schema 3 저장의 명시 변환 경로를 마련한다. 원본 JSON과 저장 해시를 대조하고, 전송 수량 항목 추가만 있는 데이터 쌍에서 새 출력 경로에 새 해시를 기록한다. 출력은 일반 로더로 다시 열어 개체 ID·체력·좌표·잔고·예약·대기열·유령 상태를 비교한다. 해시 불일치·다른 밸런스 차이·출력 경로 충돌을 거부하고 기존 Format 1 변환도 회귀 검사한다.
- [x] 관련 .NET 테스트를 실행하고 아래 검증 명령으로 빌드·전체 테스트를 확인한다. 완료 보고에는 새 프로토콜 버전과 데이터 해시 영향을 기록한다.

### 작업 2 · 키를 뗄 때 선택하고 조합으로 보내기

**파일** 수정 `Input/ControlBindings.cs`, `Input/LegionInput.cs`, `Bootstrap/ClientBootstrap.cs`, 필요한 `Legion/SquadControlState.cs`. 새 `Input/SquadShortcutState.cs`와 `.meta`. 테스트는 기존 `SquadBindingTests.cs`, `SquadControlTests.cs`, `LegionCameraTests.cs` 및 새 `SquadShortcutTests.cs`와 `.meta`.

**계약** `ControlBindings.Released(int slot) : bool`을 추가한다. `SquadShortcutState.Update(byte heldMask, byte pressedMask, byte releasedMask, bool enabled) : SquadShortcutResult`는 선택 마스크 또는 출발·목적 ID를 반환한다. 결과 형식은 같은 파일에 두며 `SelectionMask`, `FromSquad`, `TargetSquad`만 가진다. `PendingFromSquad : byte`는 HUD에 보일 출발 후보이고 0은 후보 없음이다. `Reset()`에서 후보와 대기 상태를 함께 지운다. 누름 마스크의 bit0~4는 설정된 1~5번 슬롯이다.

- [x] 위 입력 계약의 key-down/up·조합·반복·동시 누름·해제 순서와 차단을 상태 테스트로 고정한다. 첫 key-down에서 후보가 나타나고, 키 해제·차단 때 사라지는지 검사한다. 복귀 후 미해제 분대 키를 모두 뗀 뒤 새 동작을 시작한다.
- [x] 하드코딩한 Digit1 대신 기존 키 설정 슬롯의 Held/Pressed/Released를 읽는다. NumPad·재설정·구형 저장 복원을 함께 확인한다.
- [x] 선택은 기존 `SelectSquad(byte mask)` 경로로 확정한다. 빠른 전송은 출발을 인수로 받아 작업 1의 API로 보낸다. `Transfer(target,count)`를 호출하기 위해 현재 선택을 임시로 바꾸지 않는다.
- [x] `GuardChordPreservesSelectedFieldAndFocus`에서 선택 1 중 4→2의 출발 ID가 4인지, 선택 마스크·카메라·예측 대표가 1에 남는지 검사한다. 조합으로 원소를 전부 보내도 불필요한 선택 이벤트·Focus 명령이 발생하지 않게 한다.
- [x] HUD에 출발 후보·요청·성공 수량·거절 사유를 연결한다. 최신 요청 순번을 보관해 늦은 옛 응답이 새 안내를 덮지 않게 하고, 연결 변경 때 초기화한다.
- [x] B·R·G·Tab·ESC·채팅·승계와 기존 지점 이동 테스트를 실행한다. 입력 차단 때 대기 조합과 포인터 제어를 함께 해제한다.

### 작업 3 · 기본 HUD와 관리 창의 공통 상태

**파일** 수정 `UI/CombatHud.cs`, `CombatHud.Layout.cs`, `CombatHud.Squads.cs`, `ObservatoryUi.cs`, `Bootstrap/ClientBootstrap.cs`. 새 `UI/CombatHud.Panels.cs`와 `.meta`. 관련 테스트는 `MainWorldHudTests.cs`, `UiReconstructionTests.cs`, `RuntimeUiUndoTests.cs`를 확장한다.

**계약** 새 `HudPanelKind`는 `None, Training, Forces, Construction, BuildingInfo, ElementInfo`다. `CombatHud.SetPanel(HudPanelKind panel)`이 한 곳에서 전환하고, `ClosePanel() : bool`이 실제 닫은 경우를 반환한다. 입력·서버 월드 상태는 이 enum에 저장하지 않는다.

- [x] UI-01의 다섯 영역을 앵커로 배치한다. 선택·출발 후보·목적 분대의 강조를 서로 구분한다.
- [x] 접속 직후 관리 창은 닫힌 상태로 시작한다. 같은 버튼을 다시 누르면 닫고 다른 버튼을 누르면 같은 영역의 내용만 바꾼다.
- [x] 기존 헤더·왼쪽 분대 판·하단 전체 구성에서 옮긴 기능을 새 버튼과 탭에 연결한다. 자동으로 다시 켜지는 `RefreshLayout`의 이전 roster/training 제어도 수정한다.
- [x] UI 요소가 포인터를 소비하고 소개·번호 텍스트는 불필요한 raycast를 막도록 설정한다. HUD 클릭 뒤 WASD가 이어지는 회귀를 확인한다.
- [x] 여백·크기·피드백 시간을 Inspector에서 조절한다. 레이아웃 갱신과 텍스트·오브젝트 재생성은 화면 크기나 상태 변경에 맞춘다.
- [x] 기본 창과 전환·초기화·재접속 상태, 720p/110%에서 경보·스킬·미니맵 겹침을 검사한다.

### 작업 4 · 양성 창의 버튼 선택과 예약

**파일** 수정 `CombatHud.cs`, `CombatHud.Layout.cs`, 필요한 새 `UI/CombatHud.Training.cs`와 `.meta`. 기존 `TrainingBatchHudTests.cs`를 확장한다.

**계약** `TrainRequested(int element, int count, byte target)`, `TrainingCancelRequested(ulong orderId)`를 재사용한다. UI 선택값과 예약의 실제 상태를 구분한다.

- [x] UI-02처럼 자연/에너지 원소 탭과 수량·목적 분대 버튼을 만든다. `GameRules.MaxTrainingBatchCount`만큼 수량을 제공한다.
- [x] 처음 수량 1·목적 수비대 4를 기존 기본값으로 사용한다. 창을 다시 열면 같은 세션에서 고른 값을 유지하며 현재 분대 선택이 이를 덮지 않게 한다.
- [x] 원소 선택은 주문을 만들지 않는다. 확정 버튼에서 총비용·기당 시간·잠금/자원/인구/수비대 슬롯을 검사해 기존 요청을 한 번 보낸다.
- [x] 진행 항목의 남은 시간·목적과 대기 항목의 원소·목적·취소를 표시한다. 긴 목록은 이 창 안에서 스크롤하며 진행 중 취소 제한을 유지한다.
- [x] 1~4 묶음·1~4 목적, 자원/예약 경합·개별 취소·닫았다 열기·키 선택 보존·정상 네트워크 진입을 테스트한다.

### 작업 5 · 병력 창과 기존 재편·집결

**파일** 수정 `CombatHud.Squads.cs`, 필요한 새 `UI/CombatHud.Forces.cs`와 `.meta`, `ClientBootstrap.cs`. `DefenseHudTests.cs`, `MainWorldHudTests.cs`, `SquadControlTests.cs`를 확장한다.

**계약** 병력 목록은 `GameplayFrame.Squads`의 합류·증원·예약·명령을 읽는다. 기존 `TransferRequested(byte target, int count)`, `RallyRequested`, `TryGetRally(out byte target, out int count)`는 지정 수량 조작에 재사용한다.

- [x] UI-03의 1~4 목록·선택 상태·증원 상태를 표시한다. 5는 기본 HUD의 전체 선택으로 둔다.
- [x] 원소 구성 탭에 전체 소유 인원과 자연/에너지 14종 구성을 표시한다. 분대별 구성이 수신되지 않는 현재 프레임을 분대 구성으로 잘못 표시하지 않는다. 분대별 정보가 필요하면 본인 관측 계약을 검증해 추가한다.
- [x] 재편 접힘 영역에 기존 대상·지정 수량·수비대로 복귀, G 집결 대상·수량을 넣는다. 빠른 전송은 창을 열지 않고 사용한다.
- [x] 기존 정확 수량 재편·본진 범위·수비대 슬롯 검사는 해당 명령 경로에서 유지한다. 빠른 전송의 실제 수량·이동 상태와 별도로 갱신한다.
- [x] 버튼과 G의 대상·수량이 같은지, 선택 변화가 설정을 임의로 덮지 않는지, 이동 중인 증원과 합류 인원이 중복 집계되지 않는지 검사한다.

### 작업 6 · 건설 창과 배치 우선순위

**파일** 수정 `UI/ConstructionPanel.cs`, `CombatHud.Panels.cs`, `Bootstrap/ClientFlow.cs`의 취소 연결. `RecallHudTests.cs`, `UiReconstructionTests.cs`, `SquadControlTests.cs`를 확장한다.

**계약** `ConstructionPanel.SetOpen(bool)`, `CancelPlacement() : bool`, `TickPointer(ElementalVisuals visuals, bool inputAllowed)`와 기존 Build/Upgrade 이벤트를 사용한다.

- [x] UI-04의 시설 선택·비용·해금 이유를 같은 오른쪽 창에 구성한다. 본진 업그레이드와 타워의 자연/에너지·원소 선택은 필요한 항목을 고른 뒤 표시한다.
- [x] 배치 고스트·유효 범위·귀환 예약 구역 검사·서버 결과를 기존 경로와 연결한다.
- [x] 우클릭·ESC의 첫 입력은 배치를 취소한다. 같은 입력으로 이동 명령이나 메뉴 열기가 이어지지 않게 한다.
- [x] 창 닫기·다른 창 열기·채팅/모달 진입·연결 중단 시 배치를 정리한다. 소개 카드 호버가 배치 클릭을 가로채지 않게 한다.
- [ ] 잠긴 에너지 시설·최대 레벨·자기권 고정 설치·타워 원소·비용 부족·건물/귀환 슬롯 겹침·지도 경계와 클릭 관통을 검증한다.

### 작업 7 · 번호가 있는 미니맵과 이동 점

**파일** 수정 `UI/ObservatoryMap.cs`, `CombatHud.cs`, `CombatHud.Squads.cs`, `Legion/SnapshotTimeline.cs`의 필요한 관측 접근만 사용한다. 새 `ObservatoryMapTests.cs`와 `.meta`, 기존 카메라·시야 테스트를 연결한다.

**계약** 기존 `ObserveGameplay(GameplayFrame)`, `ObserveUnits(SnapshotTimeline,int)`를 재사용한다. 새 `SetSelectedSquads(byte mask)`로 선택만 전달한다. 이동 판단은 시간과 새 관측 위치를 입력받아 테스트할 수 있게 한다.

- [x] 서버가 허용한 내 분대 1~4의 위치에 번호를 놓는다. 선택 테두리·1·4 겹침·전체 선택을 처리하고 5번 표식은 만들지 않는다.
- [x] 본진 경보 중에는 기존 경보 상태를 받아 미니맵의 내 본진도 강조한다. 경보가 끝나면 기본 표식으로 돌아간다.
- [x] 연속된 최신 서버 관측의 위치 변화로 이동 점을 판정한다. 보간·카메라 이동을 유닛 이동으로 판정하지 않으며 첫 관측·순간 이동·이동 리비전 변경을 처리한다.
- [x] 점의 밝기를 천천히 바꾸고 정지하면 일반 표식으로 돌아간다. `ReduceMotion`이 켜지면 밝기를 고정하고 형태·상태로 움직임을 읽게 한다.
- [x] 번호를 점보다 위에 그린다. 번호는 고정 4개를 재사용하고, 개별 유닛마다 GameObject나 Update를 추가하지 않는다. 점 갱신과 지도 안개 텍스처 갱신을 구분한다.
- [x] 적·중립은 현재 시야의 수신 개체만 표시한다. 가시성 이탈·ID 재사용·ResetMap·재접속 때 이전 이동 캐시와 표식을 정리한다.
- [ ] 좌/우클릭·드래그·경계 이탈, 본진에서 번호가 모이는 상황, 정지/이동/양자 이동·다수 증원, 축소 동작·안개를 검사한다. 갱신 빈도와 할당은 같은 장면에서 측정한다.

### 작업 8 · 건물·원소 소개 카드

**파일** 수정 `UI/BuildingTooltip.cs`, `ConstructionPanel.cs`, `CombatHud.Panels.cs`, 필요 시 `Legion/ElementalVisuals.cs`. 새 `UI/ElementTooltip.cs`와 `.meta`. 기존 `LobbyElementTooltipTests.cs`, `UiReconstructionTests.cs` 및 새 `ElementTooltipTests.cs`와 `.meta`.

**계약** 건물의 기존 `ShowMenu`·`ShowWorld`와 `TryHoverBuilding`을 이어 쓴다. `ElementTooltip.Show(int element, int mainElement)`는 규칙에서 설명을 읽는다. 월드 호버는 현재 렌더 중인 수신 원소의 ID와 타입만 입력으로 전달한다.

- [x] UI-08/09를 같은 정보 영역에 배치한다. 7개 BuildingKind와 14개 원소 전체를 데이터로 설명한다.
- [x] 비용·시간·체력·범위·효과·6개 시너지를 기존 규칙에서 읽는다. 자기 주력의 할인과 다른 원소의 기본 비용/시간을 구분한다.
- [x] 건물 동적 상태는 관측된 개체로, 원소 기본 소개는 정의 데이터로 표시한다. 기본 최대 체력에 현재 체력 표기를 붙이지 않는다.
- [x] 관리 창이 닫혀 있을 때 월드 호버로 카드를 미리 보고 좌클릭으로 열어 둔다. 열어 둔 카드는 ×·ESC와 다른 창 전환으로 닫는다. 관리 창의 아이콘 호버는 그 창의 내부 설명만 갱신해 원소 선택·건설 버튼을 계속 클릭할 수 있게 한다.
- [x] 건설 배치 중에는 카드를 열지 않는다. 시야에서 사라진 대상·파괴·모달·포커스 이탈 때 카드를 정리한다. ESC로 닫은 호버 카드는 포인터가 대상을 다시 가리킬 때 표시한다.
- [ ] 가장 긴 한글 설명·키 이름·작은 화면과 UI 110%에서 줄바꿈·위치·가림·포인터 통과를 확인한다.

### 작업 9 · 현황판·ESC·설정과 화면 수명

**파일** 수정 `Bootstrap/ClientFlow.cs`, `UI/ClientScreen.cs`, `ClientSettingsScreen.cs`, `ClientPreferences.cs`의 필요한 부분. `UiReconstructionTests.cs`, `SquadBindingTests.cs`, `MainWorldHudTests.cs`를 확장하고 필요한 화면 흐름 테스트를 같은 테스트 폴더에 추가한다.

**계약** 기존 `BuildDashboard`, `BuildPause`, `ClientSettingsScreen.Build/Back`, `ClientBootstrap.SetInputBlocked`와 여정 확인 API를 재사용한다. 모달 수명은 `ClientFlow`가 관리하고 HUD 창은 작업 3의 상태를 사용한다.

- [x] UI-05의 내 군단·공개 순위와 여정 기록 탭을 구성한다. 순위에는 현재 공개 총점만 표시한다. 마지막 확인 기준은 기존 여정 확인 규칙에 따라 갱신한다.
- [x] Tab으로 연 현황의 탭·상성표를 포함해 key-up·로딩 취소·ESC에서 닫는다. 마우스로 연 현황과 닫기 조건을 구분한다.
- [x] UI-06의 세 버튼과 종료 확인·설정 왕복을 연결한다. 계속하기 뒤 미해제 분대 키와 이전 우드래그를 새 명령으로 처리하지 않게 한다.
- [x] UI-07의 음향·화면·표현·조작 키를 같은 프레임에 배치한다. 음향 미리 듣기·음소거·즉시 저장, 화면 적용의 기존 15초 확인·자동 복원과 수직 동기화/프레임 제한을 유지한다.
- [x] 조작 키 안내를 눌렀다 떼기·분대 키 조합과 맞추고 기존 저장·중복 검사·기본값 복원·입력 대기를 검사한다. 예시 음량 80/70/60으로 저장값을 덮지 않는다.
- [ ] 게임 접속 종료·호스트 승계·설정 중 ESC·확인창·채팅과 화면 비동기 로딩 경합을 테스트한다. 닫힌 씬의 콜백·입력 캡처·구독을 정리한다.

### 작업 10 · 프리팹·글꼴·시각 검증과 마무리

**파일** 관련 `LegionClient.prefab`, `LegionUiArt.asset`, `LegionUiSDF.asset`, `ClientSceneBuilder.cs`, `UiArtSetup.cs`, `UiPreviewRenderer.cs`, 필요한 테스트·문서. 변경 결과는 새 `Docs/Reports/2026-11-11-ui-controls.md`에 기록한다.

- [x] 새 스크립트·필드의 meta/GUID·프리팹·씬·생성기 연결을 확인한다. 현재 시설 VFX·소유자 색상·시야 관련 사용자 변경을 함께 검토한다.
- [x] 모든 새 한글·기호·실제 키 이름이 TMP 글꼴에 포함되는지 확인한다. 긴 상태가 짧은 예시 문구와 같은 상자 안에서 읽히게 한다.
- [x] 9개 화면과 양성 예약·건설 잠금·경보·R 시전/대기·겹친 번호를 720p/1080p/1440p, UI 90/100/110%에서 캡처한다. 이미지의 해상도·UI 비율·플레이 상태를 기록한다.
- [ ] 아래 기능 검증 시나리오를 실제 입력으로 확인한다. 자동 테스트·Editor 렌더·사람 Play·Player 결과는 각각 기록한다.
- [x] 문서의 UI·키 설명과 실제 구현을 맞춘다. 갱신 대상은 [UI·UX 기획](../기획서/분야별/10-ui-ux.md), [게임 규칙](../Rules/gameplay.md), [프로토콜](../protocol.md), 기존 구현 계획의 관련 작업이다.
- [x] 각 작업의 결과를 검토하고 승인 범위의 변경만 인계한다. 사용자가 커밋·푸시를 요청하면 기능별로 관련 파일을 명시해 수행한다.

## 검증 명령과 실제 플레이 시나리오

서버·공유 코드가 바뀌는 작업 1에서 관련 테스트 후 솔루션 빌드·전체 테스트를 실행한다.

```powershell
dotnet test server/Tests/ElementalLegion.Tests.csproj --filter "FullyQualifiedName~SquadCommandTests|FullyQualifiedName~SquadProtocolTests|FullyQualifiedName~SquadMigrationTests|FullyQualifiedName~ReinforcementTests|FullyQualifiedName~GuardReturnTests|FullyQualifiedName~GameRulesTests|FullyQualifiedName~WorldSaveMigrationTests" -m:1 -nodeReuse:false
dotnet build server/ElementalLegion.sln -m:1 -nodeReuse:false
dotnet test server/ElementalLegion.sln --no-build --no-restore -m:1 -nodeReuse:false
```

Unity는 `client/` 프로젝트의 Editor 컴파일과 관련 EditMode 검사를 사용한다. 실행 환경의 연결된 Editor가 이 프로젝트인지 확인하고, 설치된 도구는 [도구 목록](../tools.md)을 따른다. 이 문서 작성 때 빌드·테스트·Editor·Play는 실행하지 않았다.

| 실제 플레이 확인 | 관찰할 결과 |
| --- | --- |
| 1번 조종 중 4 held→2 | 최대 3기 출격, 1번 선택·카메라 유지, 요청 뒤 서버 확정 수량 표시 |
| 출발 2기·1기·0기 | 2기·1기는 실제로 이동, 0기는 실패 안내, 남은 병력과 인구 보존 |
| 원거리 1→3과 이동 중 사망 | 출발 위치 유지·행군·피격·도착 합류, 증원 인원과 합류 인원 일치 |
| 1~5 단독 누름·해제 | 해제 때 한 번 선택·카메라 이동, 5는 야전 전체, 수비대 별도 |
| 분대 키 또는 우드래그 중 채팅·Alt-Tab·ESC·승계 | 복귀 뒤 잘못된 선택·추가 전송·이전 지점 명령 재개가 없음 |
| 양성/병력/건설을 번갈아 열기 | 같은 영역에 하나만 표시, 원래 선택·수량 설정과 예약의 실제 상태 일치 |
| 건설 중 우클릭·ESC | 배치만 먼저 취소, 취소 입력이 이동 명령으로 전달되지 않음 |
| 본진 경보·B·R | 경보 유지, B 카메라 이동·선택 보존, R 시전·중단·공유 대기 확인 |
| Tab 현황·상성표·설정 왕복 | 원래 화면으로 복귀, 누름/해제와 입력 차단 수명 일치, 여정 확인 처리 일치 |
| 지도 번호 겹침·여러 증원·안개 | 번호가 점 위에서 읽힘, 느린 점멸·축소 동작 반영, 숨은 상대 표식이 남지 않음 |

## 다른 채팅에서 시작할 때 전달할 문장

> `Docs/Plans/2026-11-11-ui-controls.md`와 `Docs/Art/Concepts/2026-11-11-ui-index.md`를 읽고 UI·분대 단축키 개편을 구현해 주세요. 대화에서 정한 방향과 계획에서 구체화한 기본안을 함께 확인하고 작업 1부터 진행해 주세요. 현재 작업 트리에 다른 변경이 있으므로 파일 상태를 먼저 확인하고 관련 변경을 보존해 주세요. 시안의 예시 수치 대신 실제 서버 관측과 게임 데이터를 사용하고, 각 작업의 완료·검증 결과를 계획서와 보고서에 기록해 주세요.

