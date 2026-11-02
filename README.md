# Elemental-Legion
PvP 약탈형 거점 전략 + 액션 RTS (원소 진영 선택, 직접 조작)

M3 전투·성장 루프를 protocol **v3**로 연결했다. 서버가 자동 전투·14원소·6시너지·전향·핵·양성·수비대 합류를 판정하고 Unity는 명령과 HUD·원소 표현·음향을 담당한다. 실제 Play/Windows Player의 최종 아트·조작·60fps 확인 전까지 M3 전체 수용은 보류다. [M3 검증](Docs/Reports/2026-11-03-m3-validation.md)에 자동 검증과 남은 항목을 구분한다.

## 이 PC에서 실행하기 — PowerShell

명령은 **Windows PowerShell/터미널**에 입력한다. 아래 절대 경로 명령은 현재 폴더와 무관하다.

1. 이전 서버를 실행한 PowerShell에서 **Ctrl+C**로 v1/v2 서버를 종료한다. Unity도 Play를 중지한다. 같은 UDP 포트에 이전 서버를 남기면 새 클라이언트는 버전 불일치로 접속할 수 없다.
2. 일반 게임은 다음 명령으로 **최신 소스를 Release 재빌드**하고 실행한다.

```powershell
& 'C:\project\game\Elemental-Legion\Elemental-Legion\client\Tools\Start-Server.ps1' -ReportNetwork -ReportGameplay
```

3. 혼자 M3 양성·전향·드롭을 확인하려면 일반 서버를 Ctrl+C로 종료하고 테스트 모드를 실행한다.

```powershell
& 'C:\project\game\Elemental-Legion\Elemental-Legion\client\Tools\Start-Server.ps1' -Scenario m3-demo -ReportNetwork -ReportGameplay
```

4. **`M3 UDP 서버: protocol=3; scenario=normal` 또는 `scenario=m3-demo`** 배너를 확인한다. Unity의 새 스크립트 컴파일이 끝난 뒤 `Assets/_Project/Scenes/Legion.unity`에서 Play → `127.0.0.1` → **물** → 접속한다. 이미 접속 중이면 연결 종료 후 다시 접속한다. 예전 M2 Temp DLL 명령을 사용하지 않는다.
5. WASD/방향키 이동,14종 버튼으로 단일 양성, **G/집결 버튼**으로 수비대 집결. 테스트 동선은 중립 접근 → 자동 교전 → 전향/드롭·습득 → 본진에서 떨어져 양성 → 수비대 대기 → G 집결 → 본진 근처 자동 합류다. 본진·시설의 링은 실제 서버 충돌 반경이다.

일반 서버는 시작3기·0핵·양성소 없음이다. `m3-demo`만 시드1·두 양성소·에너지 양성소 최초 해금 레벨·14종 한 번씩의 실제 비용 합을 제공한다. 중립 화염은 일반/이미 전향된 상태를 구분해 각 슬롯 근처에 소수 배치한다. 확률·전투 수치는 그대로이며 초기화는 서버 재시작이다.

런처는 고정 SDK가 없으면 **설치된 .NET10 SDK를 직접 호출**하고 `global.json`을 보존한다. 이 PC의10.0.302로 검증했다. 출력은 `client/Temp/ServerRun/dotnet-artifacts`다. 실행 중인 서버 DLL을 재빌드하려면 먼저 그 서버를 Ctrl+C로 종료한다. 빌드만 하려면 `-BuildOnly`를 붙인다. 다른 서버 프로세스를 자동 종료하지 않는다.

## 개발 환경

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

M3 프로토콜 v3 UDP 서버는 다음처럼 명시적으로 실행한다. 포트·틱·전송 주기는 JSON을 사용하며 M0 봇 모드와 함께 지정하지 않는다. 이전 v1/v2 서버를 종료하고 새 빌드로 실행해야 한다.

```powershell
dotnet run --project server/Server -- --listen --report-network --report-gameplay
dotnet run --project server/Server -- --listen --scenario m3-demo --report-network --report-gameplay
```

현재 PC에는 SDK 10.0.302가 설치되어 있고 저장소의 `global.json`은 이전 PC의 10.0.401을 유지한다. 고정 SDK가 없다는 오류가 나면 설정을 임의 변경하지 않고, 이 PC의 기존 SDK를 직접 호출할 수 있다.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' 'C:\Program Files\dotnet\sdk\10.0.302\dotnet.dll' build server/ElementalLegion.sln -m:1 -nodeReuse:false
& 'C:\Program Files\dotnet\dotnet.exe' server/Server/bin/Debug/net10.0/ElementalLegion.Server.dll --listen
```

위 경로는 이 PC의 검증 환경이다. SDK 10.0.401이 있는 PC에서는 일반 명령을 사용한다.

실행 중인 서버가 DLL을 잠그면 해당 터미널에서 Ctrl+C로 종료한 뒤 빌드한다. 위 `Start-Server.ps1`은 최신 소스로 별도 출력을 만든다. 보존한 M1/M2 검증 출력은 현재 v3 클라이언트와 접속할 수 없다.

`--report-network`, `--report-gameplay`, `--scenario`는 `--listen`과 함께 사용한다. 종료 보고는 틱 밖에서 출력한다. 네트워크는 플레이어별 앱/LiteNetLib bytes·packets·평균/최대1초 KiB/s·초기/정상 비용과 패킷 종류를 구분한다. LiteNetLib는 재전송 포함/UDP·IP 헤더 제외다. 게임플레이는 초기3기를 제외한 양성·전향 누적과 `전향/(양성+전향)`을 보고하며 분모0은 N/A다. JSON60%는 비교 지표다.

M0 성능 측정은 Release에서 실행한다. 고정 시드 1로 각 시나리오를 1,000틱 워밍업하고 10,000틱 측정한다. 분산·밀집 배치의 각 표본은 750기와 중립 238기로 시작하며, 인구 감소 시 다음 표본 전에 초기화한다. 재설정 비용은 별도로 출력한다. 연속 진행은 인구 변화를 그대로 보고한다.

```powershell
dotnet run --project server/Server --configuration Release -- --bots 15 --report-tick

# 20Hz로 계속 실행. 시작 인구·자원은 JSON을 사용하고 양성소만 미리 배치한다.
dotnet run --project server/Server -- --bots 15

# 네트워크 봇은 M6에서 구현하며 현재는 실패 코드로 종료한다.
dotnet run --project server/Bots -- --bots 15 --host 127.0.0.1
```

`--bots`는 1~15명이고 `--report-tick`은 `--bots`와 함께 사용한다. 잘못된 인수는 종료 코드 2, 최대 부하 p99가 10ms를 넘으면 3을 반환한다. 실시간 실행은 `Ctrl+C`로 정리·종료한다. M0는 UDP 소켓을 열지 않는다.

최대 부하의 혼합 원소 50기·최대 기지 레벨·양성소는 측정용 설정이다. 기본 시작 인구 3기와 기존 밸런스는 바꾸지 않았다. 최소 봇은 시야 안 중립 사냥·핵 습득 → 양성 → 적 접근과 순찰만 수행한다. 수비대 집결 명령은 코어에서 지원하며, 봇의 복잡한 지휘는 구현하지 않는다.

## Unity

1. Unity Hub에 `client/` 폴더를 추가하고 **6000.6.0f1**로 연다.
2. Package Manager가 로컬 공유 패키지와 잠금 파일의 패키지를 가져올 때까지 기다린다.
3. `Assets/_Project/Scenes/Legion.unity`를 연다. `LegionClient.prefab`에 데이터·입력·카메라·UI 참조가 연결되어 있다. `Assets/_Project/Scenes/SampleScene.unity`는 보존한 URP 예제다.
4. 위 UDP 서버를 실행하고 사람이 Play를 누른다. 주소 `127.0.0.1`과 자연 원소를 선택해 **접속**한다. 방향키 또는 WASD로 군단을 이동한다. 주소 입력·원소 목록 조작 중에는 군단 입력을 막는다.
5. 두 번째 Unity 클라이언트/Windows Player에서도 같은 서버에 접속한다. 인접한 초기 본진은 최소 간격을 유지하므로 첫 두 군단은 서로를 향해 이동해야 시야에 들어온다. 아군 소유자 링은 청록색, 적은 주황색, 중립은 회색이며 몸체는 원소별 표현을 사용한다.

Play와 Windows Player의 최종 시각·입력 검사는 사람이 수행한다. 자동 검증은 같은 `ClientConnection`을 쓰는 실제 루프백 UDP 2피어, 예측·보간 로직, Unity 컴파일과 EditMode 자산 계약을 검사한다.

종료는 이동을 멈추고 군단·본진을 월드에 남긴다. 유령 만료·계정 재접속은 M4/M5 전까지 없으므로 실행당 최대15개의 새 세션 이후에는 개발 서버를 재시작한다. 이미 관측한 건물의 중심 충돌은 공유 헬퍼로 예측하고 숨겨진 건물은 서버 결과로 보정한다.

`Assets/_Project/Scenes/ElementGallery.unity`는 서버와 분리된14원소·6시너지 비교 씬이다. `Settings/Elements` 프로필에서 몸체·입자·색·크기를, 머티리얼에서 흐름·굴절을 조절한다. `ElementalVisuals`의 VFX256/보조입자32768, `CombatAudio`의 음성32 상한을 확인한다. Master/SFX/UI 음량·음소거는 HUD에서 조절한다. 시야 이탈·연결 종료 때 효과·음향을 정리한다. 최종 아트의 사람 수용은 별도다.

M2 연무는 미탐색 검정, 탐색된 지역의 어두운 지형·짙은 연무, 현재 시야의 옅은 연무를 표시한다. `SpaceFog.asset`에서 밀도·색·높이·흐름·조명·경계와 Low/Normal/High 품질을 조절한다. 기본은 반해상도32회·높이24m다. 시야 반경25m는 JSON 원본을 유지하며 표현 설정으로 바꾸지 않는다. 탐색 기록은 서버 세션 메모리에만 있고 계정 저장은 M5다.

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
