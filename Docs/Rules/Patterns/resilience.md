# 비동기·복원력·운영 패턴

이 항목은 외부 I/O와 운영 문제가 있을 때 읽는다. 시뮬레이션은 단일 스레드·고정 틱으로 유지한다. 비동기 결과는 명시적인 틱 경계에서 적용한다.

| 패턴·기법 | 역할과 적용 기준 |
| --- | --- |
| Producer–Consumer / Bounded Queue | 생산과 처리를 분리하되 큐 용량·초과 정책을 둔다. 무제한 적재하지 않는다. |
| Backpressure | 처리 가능량을 초과하면 상위 요청을 제한한다. 위치 스냅샷과 신뢰 이벤트의 유실 허용도를 구분한다. |
| Timeout / Cancellation | 작업의 대기·수명을 제한한다. 취소 뒤 늦은 결과가 월드를 수정하지 않게 한다. |
| Retry / Exponential Backoff / Jitter | 일시 실패만 제한된 횟수로 재시도한다. 부작용 있는 요청은 중복 처리 대책을 먼저 마련한다. |
| Circuit Breaker | 반복 실패하는 외부 기능을 잠시 차단한다. 명령의 자원 부족 같은 정상 거절에 적용하지 않는다. |
| Bulkhead | 외부 작업의 자원·실패 범위를 격리한다. 한 기능 장애가 월드 틱을 정지시키지 않게 한다. |
| Rate Limiting / Throttling | 요청량·자원 사용을 제한한다. 서버 명령 상한은 JSON 설정을 따른다. |
| Queue-Based Load Leveling / Priority Queue | 순간 부하를 버퍼링하거나 우선순위를 정한다. 틱 입력을 임의로 재정렬하지 않는다. |
| Saga / Compensating Transaction | 여러 독립 서비스의 변경과 보상을 관리한다. 로컬 명령 실패에 분산 트랜잭션 체계를 붙이지 않는다. |
| Health Endpoint Monitoring | 외부 감시가 상태를 확인할 경계를 제공한다. 내부 계정·월드 정보를 노출하지 않는다. |
| Leader Election / Sharding | 여러 프로세스가 실제로 필요할 때 역할·소유권을 나눈다. 현재 월드를 무단으로 분산하지 않는다. |
| Sidecar / Ambassador | 별도 프로세스에서 공통 기능을 제공한다. 추가 배포·장애 지점을 정당화할 필요가 있다. |
| Strangler Fig | 기존 시스템을 단계적으로 교체한다. 신규 환경에 불필요한 마이그레이션 계층을 만들지 않는다. |

재시도와 실패 차단의 차이는 [Retry](https://learn.microsoft.com/en-us/azure/architecture/patterns/retry), [Circuit Breaker](https://learn.microsoft.com/en-us/azure/architecture/patterns/circuit-breaker)를 참고한다. 나머지 운영 분류는 [Microsoft의 클라우드 패턴 카탈로그](https://learn.microsoft.com/en-us/azure/architecture/patterns/)를 참고한 검토 항목이며 Azure 서비스 도입 지시가 아니다.

## 추가 운영 요구가 생겼을 때의 탐색 인덱스

- API·인증 경계: Gatekeeper, Gateway Routing, Gateway Aggregation, Gateway Offloading, Backends for Frontends, Federated Identity, Valet Key.
- 배포·지역·설정: Deployment Stamps, Geode, External Configuration Store, Static Content Hosting.
- 처리 조정·자산 검사: Choreography, Sequential Convoy, Scheduler Agent Supervisor, Quarantine.
- 데이터·메시징 분야와 겹치는 Claim Check, Index Table, Cache-Aside, CQRS, Event Sourcing, Materialized View, Messaging Bridge, Pipes and Filters, Publisher–Subscriber, Idempotent Consumer는 해당 분야 문서에서 시작한다.

위 항목은 이름과 조사 경로를 보관한 것이다. 적용할 때 원문의 문제·트레이드오프를 읽고 비용·보안·운영 규모가 맞는지 확인한다. 이 목록 자체가 클라우드 도입이나 유료 서비스 사용을 승인하지 않는다.
