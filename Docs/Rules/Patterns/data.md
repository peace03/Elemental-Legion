# 데이터·저장·조회 패턴

Repository·Unit of Work·Data Mapper 등은 [Fowler의 응용 아키텍처 카탈로그](https://martinfowler.com/eaaCatalog/)를 참고한다. 저장 방식은 현재 파일 구현을 유지하며 아래 항목만으로 데이터베이스를 도입하지 않는다.

| 패턴·구조 | 역할과 프로젝트 기준 |
| --- | --- |
| Repository / Store | 게임 로직과 저장 방식을 분리한다. `IAccountStore`·`IWorldStore`에서 실패 처리와 파일 손상 방지를 포함한다. |
| Unit of Work | 한 작업에서 변경된 항목과 저장 경계를 모은다. 파일 저장에 데이터베이스 트랜잭션과 같은 보장이 있다고 가정하지 않는다. |
| Data Mapper | 저장 형식과 도메인 객체를 변환한다. 런타임 객체를 그대로 직렬화하지 않는다. |
| Active Record | 객체가 저장 동작까지 갖는 방식이다. 공유 시뮬레이션의 I/O 분리 원칙 때문에 핵심 도메인에는 적용하지 않는다. |
| Transaction Script / Domain Model / Table Module | 절차별 처리·행동을 가진 도메인·데이터 묶음 처리 중 문제에 맞게 고른다. 이름에 맞추려고 구현을 다시 쓰지 않는다. |
| Service Layer | 유스케이스의 호출 경계와 조정 절차를 제공한다. 단순 전달 계층은 생략한다. |
| Identity Map | 같은 ID가 서로 다른 상태 객체로 중복 로딩되지 않게 한다. 소유 범위와 무효화 시점을 정한다. |
| Lazy Load | 필요할 때 로딩한다. 틱 중 숨은 파일 I/O를 일으키지 않는다. |
| DTO | 전송·저장용 데이터를 내부 가변 객체와 분리한다. 시야 밖 필드를 포함하지 않는다. |
| Query Object | 반복되는 복잡한 조회 조건을 표현한다. 단순 조회까지 객체로 만들지 않는다. |
| Optimistic / Pessimistic Offline Lock | 외부 저장 동시 수정 충돌을 검출하거나 접근을 제한한다. 단일 스레드 월드 안에 불필요한 잠금을 추가하지 않는다. |
| Serialized LOB / Embedded Value / Identity Field | 저장할 객체 묶음·값·식별자의 형식을 정한다. 스키마와 데이터 크기를 검증한다. |

## 읽기·쓰기와 이벤트 이력

| 패턴·기법 | 적용 기준 |
| --- | --- |
| CQS | 상태를 바꾸는 명령과 읽기 조회를 구분한다. CQRS와 같은 대규모 구조가 자동으로 필요한 것은 아니다. |
| CQRS | 읽기·쓰기 모델을 따로 최적화할 실제 필요가 있을 때 검토한다. 별도 데이터베이스·메시지 브로커가 필수는 아니다. |
| Event Sourcing | 이벤트 이력을 상태의 원본으로 저장하고 복원한다. 일반 로그·네트워크 알림과 구분하며, 재생·버전 변환·운영 비용을 검토한다. |
| Snapshot / Checkpoint | 복원 기준 상태를 저장한다. Event Sourcing의 필수 전제나 대체 이름으로 취급하지 않는다. |
| Materialized View / Projection | 읽기용 상태를 별도 계산한다. 갱신 지연과 재생성 조건을 정한다. |
| Transactional Outbox | 데이터 변경과 전송 예정 메시지를 같은 트랜잭션으로 보관해 이중 쓰기 누락을 줄인다. 현재 파일 저장에 그대로 적용할 수 있다고 가정하지 않는다. |
| Inbox / Idempotent Consumer | 처리 ID·결과를 관리해 재전송이 중복 변경을 일으키지 않게 한다. 세션·만료 범위도 정한다. |
| Cache-Aside | 조회 시 캐시를 채운다. 원본과 무효화 정책을 정하며 게임 데이터의 두 번째 원본을 만들지 않는다. |

CQRS는 [Microsoft 설명](https://learn.microsoft.com/en-us/azure/architecture/patterns/cqrs), 이벤트 이력은 [Event Sourcing 설명](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing)을 참고한다. Outbox·Inbox·중복 처리의 관계는 [Outbox 구현 가이드](https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-out-box-cosmos), [Idempotent Consumer](https://learn.microsoft.com/en-us/azure/architecture/patterns/idempotent-consumer)를 참고한다.

새 저장 구조는 임시 파일 저장·검증·교체 등 기존 정상 데이터를 보존하는 절차와 함께 설계한다. 게임 데이터 원본과 해시 규칙은 그대로 지킨다.
