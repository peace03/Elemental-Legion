# 작업 절차와 검증

## 구현 전 요구 확인

이 절차를 구현보다 먼저 적용한다.

- 사용자가 달성하려는 목적, 원하는 사용 흐름과 동작, 작업 범위, 중요한 제약, 완료 판단 기준을 파악한다.
- 기존 대화·지정 자료·관련 코드를 먼저 읽어 확인할 수 있는 사실을 정리한다. 그 뒤에도 남는 불명확함과 해석이 갈리는 지점을 질문한다.
- 사용자가 원하는 결과를 바꿀 수 있는 조건이 불명확하면 재질문하고 답을 기다린다. 결과에 영향이 큰 질문부터 적은 수로 나누고, 사용자 답변에 따라 필요한 질문을 이어 간다.
- 이해한 요구와 중요한 조건을 짧게 정리해 사용자 의도와 일치하는지 확인한다. 답변에서 새 조건이 드러나면 이해를 갱신하고, 중요한 불명확함을 해소한 뒤 구현에 들어간다.
- 확인이 필요한 답을 임의의 기본값이나 추측으로 채워 구현하지 않는다. 질문 후 경과한 시간이나 무응답을 동의로 취급하지 않는다.
- 코드·테스트 생성이나 수정, 씬·프리팹 변경, 패키지 도입 등 구현 작업은 요구 확인을 마친 뒤 진행한다. 의도를 파악하기 위한 읽기와 분석은 먼저 수행할 수 있다.
- 구현 중에도 새로운 요구 충돌이나 결과를 바꿀 불명확함을 발견하면 해당 부분의 구현을 멈추고 재질문한다.

## 명령어

```bash
# 빌드
dotnet build server/ElementalLegion.sln -m:1 -nodeReuse:false

# 테스트 (작업을 끝내기 전에 반드시 통과해야 한다)
dotnet test server/ElementalLegion.sln -m:1 -nodeReuse:false

# 환경 확인 (데이터·해시 출력 후 종료)
dotnet run --project server/Server

# UDP 서버와 플레이어별 종료 시 네트워크·게임플레이 요약
# Windows에서는 Start-Server.cmd를 더블클릭해 일반, M3, M4, M5 데모를 선택한다.
dotnet run --project server/Server -- --listen --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m3-demo --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m4-demo --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m5-demo

# 서버 안에서 봇만으로 시뮬레이션하고 틱 시간을 보고 (M0)
dotnet run --project server/Server --configuration Release -- --bots 15 --report-tick

# 메인 월드 방어/확보·구출/최대 부하. 기존 M0와 구분한다.
dotnet run --project server/Server -c Release -- --bots 15 --report-tick --scenario main-world-defense
dotnet run --project server/Server -c Release -- --bots 15 --report-tick --scenario main-world-recovery
dotnet run --project server/Server -c Release -- --bots 15 --report-tick --scenario main-world-dense

# 구형 월드: 서버를 종료하고 원본과 다른 새 폴더로 변환한다.
dotnet run --project server/Server -c Release -- --migrate-world "<원본-worlds-폴더>" --migration-output "<새-worlds-폴더>" --source-data "<옛-gamedata.json>" --target-data shared/Data/gamedata.json

# 외부 CPU 부하가 있는 환경에서 기능 검사를 직렬로 분리할 때(시간 제한은 유지)
dotnet test server/ElementalLegion.sln -m:1 -nodeReuse:false -- xUnit.ParallelizeTestCollections=false

# 네트워크 봇 부하 테스트 (M6), 서버 틱 기록은 README의 M6 절차를 따른다.
dotnet run --project server/Bots -c Release -- --bots 15 --host 127.0.0.1 --server-trace .artifacts/m6/udp.ticks.ndjson

# 인터넷 방 자동 승계: 중계는 먼저 실행하고 서버 실행 파일의 절대 경로를 지정한다.
dotnet run --project server/Bots -c Release -- --bots 15 --relay https://<중계-주소> --host-server <서버-절대-경로> --migration-cycles 10 --migration-mode graceful --report .artifacts/migration/graceful.json
# 강제 종료는 --migration-mode crash와 별도 결과 파일을 사용한다.
```

- 위 CLI 옵션은 아직 없으면 구현 대상이다. 옵션을 추가하거나 바꾸면 이 표와 README를 같이 고친다.
- 구형 변환은 원본 JSON과 저장 해시를 함께 검증하고 새 경로에서 정상 복원된 경우에만 완료한다. 일반 로더의 검증은 완화하지 않는다. 지원하는 구형 JSON·원본 보존·미변환 운영 저장은 [README](../../README.md#구형-월드-저장-변환)에서 확인한다.
- 서버 실행 모드나 필수 옵션이 추가·변경되면 더블클릭 런처(`Start-Server.cmd` → `client/Tools/Start-Server.ps1`)의 선택 메뉴·실행 인수와 README를 함께 갱신한다.
- Unity는 사람이 에디터에서 연다. Unity 쪽 변경은 컴파일 에러가 없도록만 확인하고, 플레이 테스트 결과는 사람에게 요청한다.
- 인터넷 방 승계는 대기실·전장에서 정상 퇴장과 서버 강제 종료를 검증한다. 이전 세대의 명령·백업 거절, 마지막 사람 퇴장 후 AI·백업·프로세스 정리도 확인한다. 15명 반복 측정에서는 세대별 틱·전환 공백·되돌림·추가 백업 비용을 구분하고, 실제로 주입하지 않은 NetEm·외부망 조건을 통과로 표시하지 않는다.

## 마일스톤과 완료 조건

M0부터 순서대로 진행한다. 각 단계의 확인 항목을 통과해야 다음으로 간다.

| 단계 | 내용 | 완료 조건 |
| --- | --- | --- |
| M0 | 공유 전투 코어(14종 효과·6종 시너지) + 콘솔 서버·내부 봇 | 750기 + 중립 238기의 분산·밀집 부하 각각 p99 ≤ 10ms. 평균·GC·단계 시간·실제 활동과 준비 비용 보고. 규칙·연속 진행 테스트 통과 |
| M1 | Unity 접속, 군단 이동, 입력, 예측, 보간 | 로컬에서 2명이 접속해 서로의 군단이 보간되어 움직임 |
| M2 | 시야 필터와 안개, 탐색 기록 | 시야 밖 유닛이 패킷에 없음을 테스트로 확인. 클라이언트당 대역폭 보고 |
| M3 | M0 코어의 전투·전향·드롭·양성·수비대 합류를 클라이언트에 통합 | 새 아군 중 전향 비율을 로그로 보고 |
| M4 | 중립, 건설과 해금, 유령 기지 | 해금 비용 검증과 유령 타이머 테스트 통과 |
| M5 | LAN·인터넷 방, 재접속, 월드 저장, 근접·전체 채팅 | 대기실 권한과 방 격리, 근접 채팅 반경, 재시작 복원. 공개 전 Windows Player 두 대의 외부망 접속과 WSS 지연 검증 |
| M6 | 15명 네트워크 봇 부하 테스트, 지연·패킷 유실 시뮬레이션 | 틱 예산 안쪽 유지, 끊김 보고 |

## 작업 방식

- 요구 확인 후 독립적으로 분리할 작업은 [균형형 서브에이전트 규칙](subagents.md)에 따라 위임한다. 담당 파일·완료 조건을 정하고, 중요한 변경은 구현과 리뷰 담당을 분리한다.
- 새 기능·도구·그래픽 작업 전 [GitHub·Unity 에셋·공개 자료 조사와 활용 기준](reuse.md)을 확인한다. 후보를 분석해 허용된 원본 재사용·유료 공개 자료 참고·독자 구현·대체재 중 적합한 방식을 활용한다.
- 규칙 코드(상성, 전투, 전향, 경제, 해금, 시야)를 바꾸면 xUnit 테스트를 함께 추가한다.
- 작업을 끝내기 전에 `dotnet build`와 `dotnet test`를 통과시킨다.
- 패킷을 바꾸면 `Docs/protocol.md`를 같이 고친다.
- 작업 보고에는 바꾼 것, 추가한 임시 수치, 사람이 Unity에서 확인할 것을 적는다.
- 네임스페이스: `ElementalLegion.Shared.Data`, `ElementalLegion.Shared.Sim`, `ElementalLegion.Shared.Net`, `ElementalLegion.Server`, `ElementalLegion.Client`.
- 새 코드와 수정하는 코드에는 [코딩 규칙](coding.md)을 적용하고, 작업 종료 전에 이 문서의 완료 점검표를 확인한다.
- 직접 작성하는 설명 주석은 한글로 쓴다. 무엇을 하는지는 이름과 코드로 표현하고, 의도·조건·주의점은 주석으로 설명한다. 상세 기준은 [코딩 규칙](coding.md)의 한글 주석 항목을 따른다.

## 테스트와 변경 검증

- 새 규칙과 비단순 로직에는 관찰 가능한 결과를 검증하는 작은 테스트를 추가한다. 버그는 재현 조건을 테스트로 고정한 뒤 수정한다. 구현의 private 메서드나 클래스 개수에만 결합한 테스트는 만들지 않는다.
- 순수 규칙 테스트는 Unity 없이 실행할 수 있어야 한다. 난수는 고정 시드, 시간은 명시적인 틱·경과 시간으로 제어한다. 실제 대기나 외부 서비스에 기대지 않는다.
- 정상 사례 외에 해당 변경의 실패·경계 조건을 확인한다. 예를 들어 인구 상한, 자원 부족, 중복 명령, 시야 경계, 잘못된 ID, 데이터 해시 불일치가 있다.
- Unity 직렬화 필드나 참조를 바꾸면 기존 씬·프리팹의 값과 연결을 확인한다. 컴파일 통과를 플레이 동작이나 시각 검증 완료로 보고하지 않는다.
- C# 변경은 `dotnet build`·`dotnet test`를 실행하고, Unity에 영향을 주는 변경은 해당 프로젝트의 Editor 컴파일도 확인한다. 실행하지 못한 검증은 항목과 이유를 명시한다.

## 작업 완료 전 점검표

코딩 에이전트는 아래 항목을 직접 확인한다. 해당하지 않는 항목을 억지로 구현할 필요는 없다.

- [ ] 구현 전에 사용자 의도와 목표·기대 동작·범위·제약·완료 조건을 확인하고, 중요한 불명확함을 재질문으로 해소했는가?
- [ ] 변경 범위의 호출자·테스트·데이터·설정·씬·프리팹 연결을 확인했는가?
- [ ] GitHub·Unity 에셋과 필요한 공개 자료를 분석하고, 이용 조건·활용 방식·출처·호환성·검증 결과를 기록했는가? 제한 자료의 추가 허가·대체재·독자 구현 가능성을 검토했는가? 조사 생략 시 해당 작업에 불필요한 이유가 분명한가?
- [ ] SOLID에 맞게 책임과 의존성을 나누고, 상태의 수정 권한과 자원 소유자를 분명히 했는가?
- [ ] 기존 구현을 재사용하고, [패턴 인덱스](patterns.md)의 관련 분야에서 적합한 설계를 검토했는가? 채택 이유가 분명하고 불필요한 추상화는 없는가?
- [ ] 게임 수치의 JSON 원본과 서버 권위를 유지하고, 조절할 표현 수치에는 인스펙터 설명·범위·검증을 제공했는가?
- [ ] 클래스 책임과 복잡한 로직의 의도·단위·주의점을 한글로 설명했는가?
- [ ] 반복 경로의 탐색 비용·할당·I/O를 확인하고, 성능을 바꿨다면 관련 지표를 비교했는가?
- [ ] 입력 검증·실패 처리·구독 해제·취소·저장 데이터 보존을 처리했는가?
- [ ] [보안 규칙](security.md)에 따라 변경된 입력·권한 경계와 시야 정보·비밀값 노출을 확인했는가? 관련 거부 사례와 남은 문제를 보고했는가?
- [ ] 필요한 테스트와 빌드·Editor 검증을 수행하고, 관련 문서·패킷 정의를 갱신했는가?
- [ ] 결과 보고에서 확인한 사실과 미실행 검증, 추가한 임시 수치, 남은 한계를 구분했는가?
