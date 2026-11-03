# Graphify 사용 규칙

여러 모듈의 의존성·호출 흐름을 찾을 때 그래프로 후보를 좁힌다. 관계의 출처와 현재 동작은 해당 소스에서 확인한다. 그래프에서 관계를 찾지 못하면 `rg`로 호출자와 참조를 확인한다.

- 색인 범위는 `.graphifyignore`에서 관리한다. Unity 프로젝트의 스크립트·테스트, 서버 C#·솔루션·프로젝트, 공유 런타임 C#을 로컬에서 분석한다.
- `graphify-out/`은 로컬 생성물이다. 소스·브랜치·분석 범위가 바뀌면 다음 탐색 전에 갱신한다. Unity 씬·프리팹의 직렬화 참조와 어셈블리 설정은 원본 파일에서 확인한다.
- `query`에는 클래스·메서드 이름을 사용하고 `--budget`으로 출력을 제한한다. 두 지점의 경로는 `path`, 한 지점의 주변 관계는 `explain`을 사용한다. `INFERRED` 관계는 추정으로 취급한다.

저장소 루트에서 Graphify 0.9.63으로 실행한다. 도구가 필요한 환경에서는 `uv tool install graphifyy==0.9.63`으로 준비한다.

```powershell
$env:GRAPHIFY_BACKEND = 'none'
$env:GRAPHIFY_QUERY_LOG_DISABLE = '1'
graphify extract . --code-only --max-workers 1
graphify cluster-only . --no-label
graphify query 'UdpGameServer SimulationWorld' --budget 800
graphify path 'UdpGameServer' 'SimulationWorld'
```

결과는 `graphify-out/graph.json`과 `graphify-out/GRAPH_REPORT.md`에서 확인한다. `graphify-out/graph.html`은 시각 탐색용이며, 열면 CDN에서 뷰어 스크립트를 가져온다. [Graphify 공식 문서](https://github.com/Graphify-Labs/graphify)
