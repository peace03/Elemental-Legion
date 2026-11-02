# Elemental-Legion
PvP 약탈형 거점 전략 + 액션 RTS (원소 진영 선택, 직접 조작)

M0 공유 전투 코어와 콘솔 시뮬레이션을 구현했다. 14종 원소 효과·6종 시너지·전향·핵·최소 양성·시야·서버 내부 봇을 Unity 없이 실행한다. UDP 접속과 Unity 게임 화면은 후속 단계다. [구현 계획](Docs/Plans/2026-11-02-m0.md)과 [검증 기록](Docs/Reports/2026-11-02-m0-validation.md)을 함께 참고한다.

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
3. Console의 컴파일 오류를 확인한다. `Assets/Scenes/SampleScene.unity`는 URP 기본 장면이며 게임 화면은 아직 없다.

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
