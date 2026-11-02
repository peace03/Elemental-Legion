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
- 무료 외부 의존성은 [조사와 활용 기준](reuse.md)을 통과한 경우 요청 범위에서 도입한다. 유료 도입과 기존 기술 스택 교체 등 별도 결정이 필요한 경우에는 확인받는다.
