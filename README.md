# Elemental-Legion
PvP 약탈형 거점 전략 + 액션 RTS (원소 진영 선택, 직접 조작)

관측소 UI는 타이틀, 방 찾기, 대기실, 전장, 설정, 현황을 연결한다. 프로토콜 **v15**는 AI별 난이도 선택, 인터넷 방의 자동 서버 승계와 기존 AI 참가·인원별 전장·계정 복귀·채팅을 지원한다. [M5 검증 기록](Docs/Reports/2026-11-03-m5-validation.md) · [프로토콜](Docs/protocol.md)

## 서버 실행 — 더블클릭

저장소 루트의 **[Start-Server.cmd](Start-Server.cmd)를 더블클릭**한다. 저장소 위치나 설치된 .NET 10 SDK 경로를 명령에 직접 적을 필요가 없다. .NET 10 SDK는 설치되어 있어야 한다.

1. 실행 중인 서버는 **Ctrl+C**로 종료한다.
2. `Start-Server.cmd`를 더블클릭한다. 1번은 일반 서버, 2번은 양성·전향 M3 데모, 3번은 건설·공성 M4 데모, 4번은 **재접속·저장 M5 데모**다. 런처가 최신 소스를 Release로 빌드하고 실행한다.
3. 서버 배너에서 `protocol=14`를 확인한다. Unity에서 `Assets/_Project/Scenes/Bootstrap.unity`를 열고 Play를 누른다. 타이틀에서 이름을 입력하고 **게임 시작 → 저장한 주소 → 로컬 테스트 서버** 또는 **M5 데모 서버**를 선택한다.
4. 오른쪽 **양성 목록**에서 원소를 누른다. 양성 중에 원소를 더 누르면 핵과 인구 여유 내에서 누른 순서대로 예약된다. 대기열에서 해당 원소의 **취소**를 누르면 핵과 인구 슬롯을 돌려받는다. 기본 **G** 또는 **집결** 버튼으로 본진 수비대를 출발시킨다. 버튼 아래에 집결 중인 수와 본진 대기 수가 표시된다. 집결 도중에도 같은 버튼이나 키로 새 수비대를 출발시킬 수 있다.

일반 서버는 1레벨·3기·0핵에서 중립과 맵 핵을 탐색한다. 새로 입장하거나 패배 후 다시 시작할 때 서버가 다른 본진과 플레이어 원소에서 떨어진 자리를 고른다. 시설이나 건설 목록에 커서를 올리면 시설의 역할을 확인할 수 있다. 건설 메뉴에서 시설을 선택하고 본진 주변을 좌클릭한다. 타워를 지을 때는 원소를 고른다. 우클릭 또는 ESC로 배치 모드를 취소한다. 착공 후 남은 시간이 표시되고 완공되면 시설이 작동한다. `m3-demo`는 사전 양성소와 양성 비용을 제공한다. `m4-demo`는 1레벨에서 시작해 건설·업그레이드·양성을 확인할 초기 핵을 제공한다.

런처는 기존 `client/Tools/Start-Server.ps1`을 재사용한다. 고정 SDK가 없으면 **설치된 .NET 10 SDK를 직접 호출**하고 `global.json`을 보존한다. 출력은 `client/Temp/ServerRun/dotnet-artifacts`다. 실행 중인 서버 DLL을 재빌드하려면 먼저 그 서버를 Ctrl+C로 종료한다. PowerShell에서 `.\Start-Server.cmd -Scenario m3-demo`처럼 인수를 주면 메뉴 없이 실행한다. 빌드만 하려면 `.\Start-Server.cmd -BuildOnly`를 실행한다. 다른 서버 프로세스를 자동 종료하지 않는다.

## 방 만들기와 접속

- **같은 네트워크**에서 **방 만들기**를 누르고 방 제목·최소 인원·정원을 정한다. 친구는 **방 찾기 → 같은 네트워크**에서 제목을 보고 들어온다. 방장이 대기실을 나가면 가장 먼저 들어온 참가자가 방장을 맡는다.
- LAN과 인터넷 대기실에서 방장이 **AI 추가**와 **AI 제거**로 빈자리를 채울 수 있다. 각 AI 행 오른쪽에서 **쉬움·보통·어려움**을 따로 선택한다. 새 AI는 보통으로 추가되고 AI 제거는 마지막 행부터 적용된다. 정원, 최소 시작 인원, 맵 크기는 플레이어와 AI를 합쳐 계산한다. AI는 자동으로 준비되며 플레이어가 모두 준비하면 방장이 시작한다. 혼자 할 때는 같은 네트워크로 방을 열고 AI를 추가하면 된다. 각 AI는 별도의 적 군단으로 참가한다.
- LAN 검색은 UDP 7779, 실제 게임은 방에 배정된 UDP 포트를 사용한다. LAN은 신뢰하는 네트워크에서 사용한다. 호스트는 **방 만들기 → 저장한 LAN 방 다시 열기**에서 가장 최근의 미종료 방을 복원할 수 있다. 나머지 참가자는 LAN 목록에서 다시 접속한다.
- **방 만들기 → 인터넷**을 선택하면 방장 PC에서 서버가 자동으로 실행된다. 비밀번호 칸을 비워 누구나 입장할 수 있게 하거나, 숫자 4~8자리로 잠글 수 있다. 참가자는 **방 찾기 → 인터넷**에서 제목을 누른다. 잠긴 방에서만 비밀번호를 입력한다.
- 인터넷 방의 전투와 시야 판정은 방장 PC의 서버가 맡는다. 참가자들은 운영 중계에 연결하므로 공유기 포트 설정 없이 입장할 수 있다. 사람 참가자가 남아 있으면 예비 서버를 준비해 방장 종료 시 서버를 넘긴다. 강제 종료에서는 최근 백업 이후 진행이 일부 되돌아갈 수 있다. 마지막 사람이 나가면 방을 종료한다. 살아 있는 방에는 같은 계정으로 재접속할 수 있다.
- 전장에서 `Enter`로 채팅을 연다. 입력 중 `Tab`으로 근접·전체를 바꾸고 `Enter`로 전송하거나 빈 입력창을 닫는다. `Esc`로 취소한다. 받은 메시지는 기본 6초 동안 반투명하게 표시된다. 발신자 차단은 로컬에 적용하며 신고는 운영 서버가 판정하는 방에서 접수한다.
- 인터넷·LAN 방의 맵 면적은 시작 인원에 비례한다. 2인 방은 240×240m이며, 재접속해도 처음 정한 크기를 유지한다. 전장 카메라에는 지형 전체를 표시하고 미니맵에 현재 시야와 탐색 기록을 남긴다.

### 다른 집 친구와 테스트

1. [Start-Server.cmd](Start-Server.cmd)를 더블클릭하고 **5번 인터넷 친구 테스트**를 고른다. 런처가 서버를 빌드하고 Cloudflare Quick Tunnel 도구를 내려받아 SHA-256을 검증한다. 중계 서버와 터널을 실행한 뒤, 공개 주소에서 방 목록이 응답하면 **인터넷 친구 테스트 준비 완료**를 표시한다.
2. 그 창을 켜 둔 채 Unity Play를 시작하거나 Windows x64 Player를 빌드한다. Unity 에디터는 임시 주소와 5번 실행기가 빌드한 방 서버를 함께 사용한다. Windows 빌드에는 주소 파일과 방 서버가 포함된다. `ClientFlow`의 **Online Service Url**에 고정 주소를 설정했다면 그 주소를 우선 사용한다.
3. 친구에게 Windows 빌드 폴더 전체를 전달한다. 게임에서 **방 만들기 → 인터넷**으로 방을 열면 친구는 **방 찾기 → 인터넷**에서 제목을 눌러 들어온다. 친구와 둘이 테스트할 때는 숫자 비밀번호로 방을 잠글 수 있다.

실행기 창에서 **Enter**를 눌러 중계와 터널을 종료한다. 다시 실행하면 주소가 바뀐다. 이미 배포한 테스트 빌드에는 새 `.artifacts/relay-test/relay-test-url.txt`를 각 PC의 `<게임명>_Data/relay-test-url.txt`에 복사하고 게임을 다시 실행한다. Unity Editor는 Play를 다시 시작하면 새 주소를 읽는다.

Quick Tunnel의 공개 구간은 HTTPS/WSS이며, PC 안에서는 터널이 `127.0.0.1`의 자동 배정 포트로 연결한다. 테스트 데이터는 `.server-data/relay-test`에 따로 저장한다. 이 모드는 임시 공개 주소를 사용한다. [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/)

### 온라인 서버 운영

배포자는 `--online`을 실행하는 HTTPS/WSS 중계 주소를 `Bootstrap.unity`의 `ClientFlow` **Online Service Url**에 한 번 설정한다. Windows x64 Player를 빌드하면 편집기가 `Publish-LanServer.ps1`로 .NET 런타임과 방 서버를 `<게임명>_Data/Server`에 함께 넣는다. 빌드하는 PC에는 .NET 10 SDK와 NuGet 복원 환경이 필요하다. 두 플레이어에게 같은 Player 출력 폴더를 배포한다. 플레이어에게 필요한 것은 이 게임 설치본이다.

`--online`은 TLS 인증서와 개인 키가 담긴 PKCS#12 파일을 `ELEMENTAL_LEGION_CERT_PATH`로 읽는다. 암호가 있으면 `ELEMENTAL_LEGION_CERT_PASSWORD`를 서버 프로세스의 비밀 환경 변수로 전달한다. `ELEMENTAL_LEGION_ONLINE_WORLD_TITLE`에 제목을 지정하면 상시 월드를 공개 목록에 함께 표시한다. `ELEMENTAL_LEGION_DISABLE_ROOM_CREATION=1`로 이용자의 새 방 생성을 중지할 수 있다.

```powershell
dotnet run --project server/Server -c Release -- --online --port 443 --save-dir <운영-전용-저장-폴더>
dotnet run --project server/Server -c Release -- --revoke-account <계정-ID> --save-dir <운영-전용-저장-폴더>
```

외부에는 HTTPS/WSS 443만 열고 UDP·저장·신고·관리 경로는 운영 권한으로 보호한다. 운영 서버가 판정하는 방과 상시 월드는 별도 체크포인트를 쓰며 신고 본문은 `reports`에 7일 동안 보관한다. 플레이어가 연 인터넷 방은 사람 참가자가 남아 있고 중계가 살아 있는 동안 서버를 승계한다. 방 종료 후의 이어 하기는 제공하지 않는다. 공개 전 검증은 [M5 계획의 완료 기준](Docs/Plans/2026-11-03-m5-online-rooms.md#완료-기준과-공개-순서)을 따른다.

현재 PC의 5번 중계에서 상시 서버로 옮길 때는 기존 방을 모두 마친다. 상시 서버에 같은 Release 중계와 유효한 HTTPS 인증서를 배치하고, 서버 프로세스에만 인증서 경로·암호를 주입해 위 `--online` 명령을 실행한다. `/rooms`가 HTTPS로 응답하고 WSS 연결이 가능한지 확인한 뒤 `ClientFlow`의 **Online Service Url**을 새 주소로 바꾸어 Windows Player를 다시 배포한다. 설정이 비었을 때 대신 사용하는 `<게임명>_Data/relay-test-url.txt`는 새 배포본에서 제거한다. 두 PC가 새 목록에서 같은 방에 입장하고 준비·시작·방장 퇴장 후 이동까지 이어지는지 확인한다. 새 주소는 새 방부터 사용하며 이미 열린 방을 중계 사이로 옮기지는 않는다. 중계 PC가 종료되면 그 중계의 방도 종료된다.

## 개발 환경

이 프로젝트에서 사용하는 도구와 설치 버전은 [설치 도구 목록](Docs/tools.md)에서 확인한다.

- Unity **6000.6.0f1**: Unity Hub에서 저장소 루트가 아닌 `client/`를 연다.
- .NET SDK **10.0.401**: `global.json`으로 같은 기능 버전의 패치 업데이트만 허용한다.
- 공유 코드: **.NET Standard 2.1 / C# 9 / Nullable disable**, UnityEngine 참조 없음.
- LiteNetLib **1.3.5**: NuGet의 안정 1.x 중 최신 버전을 확인해 고정했다. 서버는 NuGet, Unity는 `shared/Plugins/LiteNetLib.dll`의 동일 버전을 사용한다.
- Newtonsoft.Json: 서버 **13.0.4**, Unity 패키지 `com.unity.nuget.newtonsoft-json` **3.2.2**.
- 클라이언트: URP **17.6.0**, VFX Graph **17.6.0**, URP에 포함된 Shader Graph, Input System.
- 테스트: xUnit. NuGet `packages.lock.json`과 Unity `Packages/packages-lock.json`을 함께 관리한다.

## 빌드와 실행

저장소 루트에서 실행한다.

```powershell
dotnet restore server/ElementalLegion.sln --locked-mode
dotnet build server/ElementalLegion.sln -m:1 -nodeReuse:false
dotnet test server/ElementalLegion.sln -m:1 -nodeReuse:false
dotnet run --project server/Server
```

마지막 명령은 공유 데이터를 읽고 틱 주기·포트·SHA-256을 출력한 뒤 종료하는 환경 확인용이다. UDP 포트를 열거나 게임을 진행하지 않는다.

프로토콜 v15 UDP 서버는 다음처럼 실행할 수도 있다. 포트·틱·전송 주기는 JSON을 사용한다. 서버와 클라이언트 모두 최신 소스로 갱신한다.

```powershell
dotnet run --project server/Server -- --listen --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m3-demo --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m4-demo --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m5-demo
```

실행 중인 서버가 DLL을 잠그면 해당 터미널에서 Ctrl+C로 종료한 뒤 빌드한다.

`--report-network`, `--report-gameplay`, `--scenario`는 `--listen`과 함께 사용한다. 종료 보고는 틱 밖에서 출력한다. 네트워크는 플레이어별 앱/LiteNetLib bytes·packets·평균/최대1초 KiB/s·초기/정상 비용과 패킷 종류를 구분한다. LiteNetLib는 재전송 포함/UDP·IP 헤더 제외다. 게임플레이는 초기3기를 제외한 양성·전향 누적과 `전향/(양성+전향)`을 보고하며 분모0은 N/A다. JSON60%는 비교 지표다.

M0 성능 측정은 Release에서 실행한다. 고정 시드 1로 각 시나리오를 1,000틱 워밍업하고 10,000틱 측정한다. 분산·밀집 배치의 각 표본은 750기와 중립 238기로 시작하며, 인구 감소 시 다음 표본 전에 초기화한다. 재설정 비용은 별도로 출력한다. 연속 진행은 인구 변화를 그대로 보고한다.

```powershell
dotnet run --project server/Server --configuration Release -- --bots 15 --report-tick

# 20Hz로 계속 실행. 시작 인구·자원은 JSON을 사용하고 양성소만 미리 배치한다.
dotnet run --project server/Server -- --bots 15

```

`--bots`는 1~15명이고 `--report-tick`은 `--bots`와 함께 사용한다. 잘못된 인수는 종료 코드 2, 최대 부하 p99가 10ms를 넘으면 3을 반환한다. 실시간 실행은 `Ctrl+C`로 정리·종료한다. M0는 UDP 소켓을 열지 않는다.

최대 부하의 혼합 원소 50기·최대 기지 레벨·양성소는 측정용 설정이다. 기본 시작 인구는 3기다. 측정용 봇은 시야 안 중립 사냥·핵 습득·양성·적 접근과 순찰을 수행한다. 대기실 AI는 일반 시작 규칙으로 포집기와 양성소를 짓고, 양성한 원소를 집결시키며 관측한 적 본진을 공격한다.

### M6 네트워크 부하

UDP 일반 서버와 인터넷 방에 실제 연결을 각각 15개 연다. 테스트 전용 저장 경로, 새 표본 파일과 같은 Release 빌드를 사용한다. 기본 실행은 워밍업 30초와 측정 300초다. Linux 반복 실행과 외부 중계 명령은 [M6 실행 및 검증](Docs/Reports/2026-11-03-m6-validation.md)을 따른다.

```powershell
# 첫 터미널
New-Item -ItemType Directory -Force .artifacts/m6 | Out-Null
$env:ELEMENTAL_LEGION_LOAD_TRACE = '.artifacts/m6/udp.ticks.ndjson'
dotnet run --project server/Server -c Release -- --listen --save-dir .artifacts/m6/udp-store

# 둘째 터미널
dotnet run --project server/Bots -c Release -- --bots 15 --host 127.0.0.1 --server-trace .artifacts/m6/udp.ticks.ndjson --report .artifacts/m6/udp.json
```

파일 이름과 저장 경로는 실행마다 새로 지정한다. `--reconnect`를 붙이면 측정이 끝난 뒤 마지막 참가자의 복귀를 확인한다. `--help`로 전체 옵션을 본다. 종료 코드 0은 통과, 1은 실행 환경 오류, 2는 인수 오류, 3은 측정 실패다.

인터넷 방 승계 부하는 정상 인계와 서버 강제 종료를 각각 10회 실행한다. HTTPS 중계는 먼저 실행하고, `--host-server`에는 같은 소스의 Release 실행 파일 또는 DLL 절대 경로를 지정한다. DLL 실행에는 .NET 10 런타임이 필요하다. Windows 배포본의 자체 포함 EXE는 별도 런타임을 요구하지 않는다.

```powershell
$relay = 'https://<중계-주소>'
dotnet build server/Server -c Release
$hostServer = (Resolve-Path 'server/Server/bin/Release/net10.0/ElementalLegion.Server.dll').Path
dotnet run --project server/Bots -c Release -- --bots 15 --relay $relay --host-server $hostServer --migration-cycles 10 --migration-mode graceful --report .artifacts/migration/graceful.json
dotnet run --project server/Bots -c Release -- --bots 15 --relay $relay --host-server $hostServer --migration-cycles 10 --migration-mode crash --report .artifacts/migration/crash.json
```

정상 퇴장한 봇은 같은 계정으로 복귀하므로 각 회차는 다시 15명에서 시작한다. JSON은 세대별 틱 비용·할당, 백업 크기·압축·전송 비용, 참가자별 공백과 실제 되돌림, 요청하지 않은 세대 변경과 연결 종료를 구분한다. `FreezeToBaselineP95Ms`는 클라이언트가 전환 알림을 받은 시점부터 완성 기준 상태 수신까지이며, `LastPoseToBaselineP95Ms`는 마지막 정상 위치 수신부터의 보수적인 공백이다. `--profile netem`은 네트워크 조건을 주입하지 않으므로 실제 Linux `tc netem` 설정과 함께 사용한다. 추가 백업 바이트는 압축 본문 기준이며 TLS·WSS 헤더를 제외한다. 측정용 `.snapshot.br`에는 전체 월드가 들어 있으므로 결과 JSON과 달리 외부에 공개하지 않는다.

[2026-11-05 승계 검증](Docs/Reports/2026-11-05-host-migration-validation.md)에 로컬 TLS 15인 정상·강제 전환 각각 10회의 측정값, 실패 회귀와 남은 Unity·외부망 검증을 기록했다.

## Unity

1. Unity Hub에 `client/` 폴더를 추가하고 **6000.6.0f1**로 연다.
2. Package Manager가 로컬 공유 패키지와 잠금 파일의 패키지를 가져올 때까지 기다린다.
3. `Assets/_Project/Scenes/Bootstrap.unity`에서 Play를 누른다. 전투만 따로 확인하려면 `Legion.unity`에서 기존 주소 입력으로도 접속할 수 있다.
4. **설정 → 조작 키**에서 이동 기본·보조 키, 집결, 메뉴와 현황 키를 바꾼다. 다른 조작과 같은 키를 고르면 해당 조작을 알려 준다. 키는 이 PC에 저장되며 **기본값 복원**으로 WASD·방향키·G·ESC·Tab을 되돌릴 수 있다.
5. 두 번째 Unity 클라이언트/Windows Player에서도 같은 서버에 접속한다. 두 화면의 미니맵에서 시작 위치를 확인하고 서로 향해 이동한다. 아군 소유자 링은 청록색, 적은 주황색, 중립은 회색이며 몸체는 원소별 표현을 사용한다.

Play와 Windows Player의 최종 시각·입력 검사는 사람이 수행한다. 자동 검증은 같은 `ClientConnection`을 쓰는 실제 루프백 UDP 2피어, 예측·보간 로직, Unity 컴파일과 EditMode 자산 계약을 검사한다.

일반 서버·M5 데모·LAN 및 인터넷 방에서 연결이 끊기면 군단과 본진이 유령으로 남아 제자리에서 방어한다. 같은 Windows 사용자로 같은 서버·방에 다시 입장하면 유령 기지가 남아 있는 동안 복귀한다. 유령은 서버에서 600초가 지나면 핵으로 변환된다. 일반 서버와 M5 데모는 각각 저장 경로를 사용하며, M5 데모 포트는 7778이다. 월드는 기본 60초마다 그리고 정상 종료 시 저장한다. 공성에서 패배하면 **다시 시작**으로 방 목록에 돌아간다.

`Assets/_Project/Scenes/ElementGallery.unity`는 서버와 분리된 14원소·6시너지 비교 씬이다. `Settings/Elements` 프로필에서 몸체·입자·색·크기를, 머티리얼에서 흐름·굴절을 조절한다. 전체·효과음·UI 음량과 음소거는 타이틀 또는 전장의 **설정**에서 조절한다. **Tab**을 누르는 동안 **현황**이 열리고 키를 떼면 닫힌다. 현황판은 전장 화면 위에 열리며 왼쪽은 내 상세 상태, 오른쪽은 접속 순위와 합산 점수다. **상성표**는 타이틀이나 전장에서 버튼으로 연 현황에서 확인할 수 있다. 메뉴나 현황을 열어도 서버는 진행하며 로컬 군단 명령은 잠시 차단된다.

메인 월드는 화면에 들어온 지형을 탐색 여부와 관계없이 표시한다. 미니맵에서 미탐색·누적 탐색·현재 시야를 구분한다. 적과 중립 표시는 서버의 현재 관측을 따른다. 유령 기지로 복귀할 때 탐색 기록도 이어진다. 장식 연무 품질과 UI 크기는 설정에서 조절한다.

상성 화면의 **상성 그래프**에서는 자연·에너지 고리에서 원소를 눌러 유리한 상대와 불리한 상대, 교차 상성을 확인한다. **피해 배율표**를 선택하면 전체 공격 원소와 방어 원소의 조합을 비교할 수 있다.

[M2 검증 보고서](Docs/Reports/2026-11-02-m2-validation.md)에 대역폭·M0 전후·GPU 측정과 한계를 기록한다. 실제 Play/Windows Player의 연무 품질·경계·파티클·UI·두 화면 시야 분리·60fps는 사람이 확인할 항목으로 보류한다.

직접 작성하는 Unity 자산은 `Assets/_Project`에 배치한다. [폴더 규칙](Docs/Rules/project.md#unity-assets-내부-구조)과 [외부 자료 고지](Docs/third-party.md)를 따른다. 폴더 이동 시 GUID를 보존하며 TMP 원본 자산과 외부 폰트는 별도 원본 경로에 유지한다.

`client/Packages/manifest.json`은 `com.elementallegion.shared`를 `file:../../shared`로 참조한다. `shared/Data/gamedata.json`은 서버 출력 폴더에 복사되며 Unity에서는 `Packages/com.elementallegion.shared/Data/gamedata.json`의 TextAsset으로 읽는다.

패키지 연결을 다시 설치해야 할 때만 아래 첫 번째 명령을 실행한다. 일반적으로는 저장된 manifest와 잠금 파일로 자동 복원된다. 두 번째 명령은 별도 Editor 프로세스에서 공유 어셈블리·TextAsset·LiteNetLib를 확인하며 PC API 호환성을 .NET Standard로 저장한다. 실행 전 이 프로젝트의 Editor를 닫는다.

```powershell
$unityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe'
Start-Process -FilePath $unityEditor -Wait -WindowStyle Hidden -ArgumentList "-batchmode -nographics -projectPath `"$PWD/client`" -executeMethod ElementalLegion.Client.Editor.ProjectBootstrap.InstallPackages -logFile `"$PWD/unity-packages.log`""
Start-Process -FilePath $unityEditor -Wait -WindowStyle Hidden -ArgumentList "-batchmode -nographics -projectPath `"$PWD/client`" -executeMethod ElementalLegion.Client.Editor.ProjectBootstrap.Validate -logFile `"$PWD/unity-validation.log`""
```

설치 메서드는 비동기 Package Manager 작업이 끝나면 직접 종료하므로 `-quit`을 추가하지 않는다. 검증 결과는 `client/Library/BootstrapValidation.txt`와 로그에서 확인할 수 있다. Play 테스트와 Windows Player 빌드는 별도로 확인해야 한다.

## 작업 기준

- [에이전트 규칙 인덱스](AGENTS.md) — 작업에 필요한 상세 규칙만 선택해 읽는다.
- [기획 요약](Docs/design-summary.md)
- [원본 기획서](Docs/기획서/Elemental%20Legion%20게임%20기획서.md)
- [네트워크 프로토콜 상태](Docs/protocol.md)

게임 수치의 단일 원본은 `shared/Data/gamedata.json`이다. M0 승인 사항으로 드롭 내림 정책, 전향 체력 비율 1.0, 핵 습득 반경 1.5m, 모래폭풍 선행 타격 인정 시간 4초를 추가했다. 새 숫자 경로는 `_meta.placeholderPaths`에 기록했다. 기존 수치는 유지한다. 데이터 해시는 UTF-8 텍스트의 줄바꿈을 LF로 정규화한 뒤 계산한다. `_`로 시작하는 설명 키는 규칙에서 사용하지 않으며 해시 계산에는 원문 전체를 포함한다.
