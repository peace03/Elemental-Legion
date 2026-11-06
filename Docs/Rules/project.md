# 프로젝트 구성과 기술 스택

## 프로젝트 개요

- 장르: 실시간 PvP 군단 전략(3D 탑다운, PC). 서버 한 곳에 최대 15명이 들어오고, 유저가 계속 입퇴장하는 지속형 월드다.
- 플레이어는 자연 원소 하나를 골라 시작하고, 방향키로 군단 전체를 움직이며, 처치한 적을 전향시켜 세력을 키우고 본진을 지킨다.
- 비주얼은 모델 없이 Particle System, VFX Graph, Trail/Line Renderer, Shader Graph, Post Processing으로만 만든다.
- 서버 권위형 구조다. 서버가 월드 상태의 유일한 주인이고, 클라이언트는 명령만 보내며 시야 안의 정보만 받는다.

## 저장소 구조

```
ElementalLegion/
├─ AGENTS.md                 ← 이 문서
├─ README.md                 ← 사람이 읽는 실행 방법
├─ Docs/
│  ├─ design-summary.md      ← 기획 요약
│  └─ protocol.md            ← 패킷 정의 (패킷을 바꾸면 반드시 같이 고친다)
├─ shared/                   ← 공유 시뮬레이션. Unity 로컬 패키지 겸용
│  ├─ package.json           ← name: com.elementallegion.shared
│  ├─ Data/
│  │  └─ gamedata.json       ← 모든 게임 수치의 단일 원본
│  └─ Runtime/
│     ├─ ElementalLegion.Shared.asmdef
│     ├─ Data/               ← gamedata.json을 담는 클래스와 로더
│     ├─ Sim/                ← 월드, 군단, 유닛, 전투, 전향, 경제, 건설, 시야
│     └─ Net/                ← 패킷 정의와 직렬화
├─ server/
│  ├─ ElementalLegion.sln
│  ├─ Shared.Build/          ← shared/Runtime 소스를 .NET 쪽에서 컴파일하는 프로젝트
│  ├─ Server/                ← .NET 10 콘솔 서버
│  ├─ Bots/                  ← 네트워크 봇 클라이언트, 부하 테스트
│  └─ Tests/                 ← xUnit 테스트
└─ client/                   ← Unity 6000.6.0f1 프로젝트
   ├─ Assets/
   └─ Packages/manifest.json ← "com.elementallegion.shared": "file:../../shared"
```

### 공유 코드 연결 방식

- `shared/Runtime/`의 C# 소스는 서버와 클라이언트가 함께 쓴다.
- 서버 쪽: `server/Shared.Build/ElementalLegion.Shared.csproj`가 `<Compile Include="../../shared/Runtime/**/*.cs" />`로 같은 소스를 컴파일한다. `shared/` 폴더 안에는 csproj, bin, obj를 만들지 않는다(Unity가 가져와 버린다).
- Unity 쪽: `client/Packages/manifest.json`에서 `shared/`를 로컬 패키지로 참조한다.
- `shared/Data/gamedata.json`은 서버 빌드 출력 폴더로 복사되고(csproj의 `CopyToOutputDirectory`), Unity에서는 패키지 안의 TextAsset으로 읽는다.

## 기술 스택과 버전

| 항목 | 버전·설정 |
| --- | --- |
| Unity | 6000.6.0f1 |
| 서버 | .NET 10 (`net10.0`), 콘솔 앱 |
| 공유 코드 | `netstandard2.1`, `LangVersion 9.0`, `Nullable disable` |
| 네트워크 | LiteNetLib. NuGet의 최신 안정 1.x 버전을 확인해 **정확한 버전 번호로 고정**하고, Unity 쪽에도 같은 버전을 넣는다 |
| JSON | Newtonsoft.Json. 서버는 NuGet, Unity는 `com.unity.nuget.newtonsoft-json` 패키지 |
| 테스트 | xUnit (`server/Tests`) |

- 공유 코드의 asmdef는 `noEngineReferences: true`, `overrideReferences: true`, `precompiledReferences`에 Newtonsoft.Json과 LiteNetLib를 지정한다.
- 공유 코드는 C# 9 문법만 쓴다. `record struct`, 전역 using, 파일 범위 네임스페이스 같은 C# 10 이상 문법은 쓰지 않는다.
- 외부 의존성과 공개 참고 자료는 [조사와 활용 기준](reuse.md)을 통과한 경우 요청 범위에서 활용한다. 새 유료 구매·구독과 기존 기술 스택 교체 등 별도 결정이 필요한 경우에는 확인받는다.

## Unity Assets 내부 구조

직접 작성한 클라이언트 코드와 자산은 `client/Assets/_Project/` 아래에 둔다. 외부 패키지와 템플릿 자료는 고유 경로를 존중한다. 아래는 배치 기준이며 아직 쓰지 않는 빈 폴더를 전부 만들지 않는다.

```text
Assets/_Project/
├─ Scenes/                  # 게임 실행 씬과 기존 예제 씬
├─ Scripts/
│  ├─ Runtime/
│  │  ├─ Bootstrap/         # 시작·종료와 명시적인 의존성 연결
│  │  ├─ Networking/        # 접속과 수신 상태의 클라이언트 수명
│  │  ├─ Input/             # 플레이어 입력 → 명령 의도
│  │  ├─ Legion/            # 군단 표시, 중심 예측·보정, 상태 보간
│  │  ├─ Camera/            # 카메라 표현
│  │  └─ UI/                # 접속 화면과 HUD
│  └─ Editor/               # 자산 구성·검증 등 Editor 전용 도구
├─ Tests/
│  ├─ EditMode/             # 순수 클라이언트 로직·설정 검증
│  └─ PlayMode/             # 필요 시 Unity 수명·통합 동작 검증
├─ Prefabs/                 # 재사용할 클라이언트 오브젝트
├─ VFX/                     # Particle System·VFX Graph 자산
├─ Materials/
├─ Shaders/
├─ Audio/
└─ Settings/
   ├─ Input/                # Input Actions와 입력 설정
   └─ Rendering/            # URP·Volume 등 렌더링 자산
```

- 코드·폴더·자산 이름은 공백 없는 영문 `PascalCase`를 기본으로 한다. 네임스페이스는 `ElementalLegion.Client`와 역할별 하위 이름을 사용한다.
- 런타임 어셈블리는 `ElementalLegion.Client`, Editor 전용은 `ElementalLegion.Client.Editor`로 구분한다. 테스트는 EditMode/PlayMode 실행 환경에 맞는 별도 asmdef를 둔다. 기능별 asmdef는 실제 의존성·컴파일 비용이 필요할 때만 추가한다.
- `Client → Shared` 방향을 유지한다. `Shared`는 Unity·Client·Server를 참조하지 않는다. Client 런타임에 Editor API를 넣지 않는다.
- 게임 규칙·수치·공통 패킷과 직렬화는 기존 `shared/`에서 관리한다. 클라이언트 자산에 밸런스 복사본을 만들지 않는다. Unity 전용 예측·보간은 클라이언트에 둔다.
- Input Actions는 프로젝트에서 사용하는 기존 자산을 재사용한다. 씬·프리팹은 필요한 참조를 직렬화하고, 시작 지점에서 수명과 의존성을 연결한다.
- 기존 파일과 폴더를 옮길 때는 Unity `AssetDatabase.MoveAsset` 또는 Project 창을 사용해 `.meta`와 GUID를 보존한다. 이동 후 Build Settings·Input Actions·URP 참조·씬/프리팹 연결과 컴파일을 확인한다.
- 외부 에셋은 공급자의 업데이트 경로를 보존하고 `_Project`의 직접 작성 자산과 구분한다. 외부 폴더 전체를 임의 이동하거나 수정하지 않는다.
- `Resources`나 `StreamingAssets`는 실제 로딩 요구가 생길 때만 도입한다. 미래 기능을 위한 빈 관리자·인터페이스·폴더를 미리 늘리지 않는다.

구조 원본은 이 절이다. 채택 근거는 [Unity 공식 프로젝트 정리 가이드](https://unity.com/how-to/organizing-your-project), 구현과 검증 범위는 해당 작업 계획·보고서에 기록한다.
