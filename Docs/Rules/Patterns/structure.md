# 아키텍처·의존성·모듈 패턴

이 문서는 선택지다. 실제 프로젝트의 [아키텍처 규칙](../architecture.md)이 우선하며 패턴 검토만으로 기술 스택을 교체하지 않는다.

| 패턴·구조 | 역할과 적용 기준 |
| --- | --- |
| Layered Architecture | 표현·응용·도메인·인프라의 책임을 구분한다. 모든 호출에 빈 전달 계층을 추가하지 않는다. |
| Hexagonal / Ports and Adapters | 시뮬레이션은 포트를 통해 외부 기능을 사용하고 Unity·네트워크·파일 구현은 어댑터에 둔다. |
| Clean / Onion Architecture | 핵심 규칙을 외부 프레임워크로부터 분리하고 의존성이 안쪽을 향하게 한다. 이름이 유사한 구조를 중복 계층으로 구현하지 않는다. |
| Modular Monolith | 한 프로세스 안에서 전투·경제·저장 등의 모듈 경계를 유지한다. 각 월드의 명시적인 틱 흐름을 보존한다. |
| Microservices | 독립 배포·운영이 실제로 필요할 때만 검토한다. 최대 15명 월드를 이유 없이 여러 서비스로 쪼개지 않는다. |
| Event-Driven Architecture | 확정된 변화로 후속 처리를 연결한다. 모든 함수 호출을 이벤트로 바꾸는 지시가 아니다. |
| Microkernel / Plugin | 실제로 외부 확장 모듈을 배포·교체할 필요가 있을 때 검토한다. 현재 요구에 없는 플러그인 체계를 먼저 만들지 않는다. |
| Dependency Injection / Composition Root | 필요한 의존성을 외부에서 전달하고 시작 지점에서 연결한다. DI 컨테이너 없이 생성자 연결로 충분한지 먼저 확인한다. |
| Service Locator | 의존성을 조회하는 방식이다. 의존성이 숨겨지므로 핵심 로직에는 사용하지 않고 기존 외부 API 연결이 불가피한 경계에서만 제한한다. |
| Functional Core / Imperative Shell | 계산 가능한 규칙은 순수 계산으로, I/O와 상태 적용은 바깥쪽 조정 코드로 분리한다. |
| Actor Model | 상태 소유자별 메시지 처리를 직렬화한다. 도입하더라도 월드 상태를 여러 스레드가 직접 수정하게 하지 않는다. |
| Anti-Corruption Layer | 외부 시스템의 데이터·용어가 도메인으로 번지지 않도록 변환한다. 단순 매핑이면 작은 Adapter로 시작한다. |

Hexagonal은 [원저자 설명](https://alistair.cockburn.us/hexagonal-architecture), Clean·Onion의 관계와 의존성 방향은 [Clean Architecture 원문](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html), DI와 Locator는 [Fowler의 비교](https://martinfowler.com/articles/injection.html)를 참고한다. Actor는 [Microsoft Orleans의 개념 설명](https://learn.microsoft.com/en-us/dotnet/orleans/overview), Functional Core / Imperative Shell은 [Gary Bernhardt의 설명](https://www.destroyallsoftware.com/screencasts/catalog/functional-core-imperative-shell)을 참고한다. 특정 프레임워크 도입을 뜻하지 않는다.

## 도메인 경계

| 패턴·개념 | 적용 기준 |
| --- | --- |
| Bounded Context | 같은 단어의 의미와 규칙이 달라지는 경계를 명시한다. 폴더를 나눈 것만으로 서로 다른 문맥이라고 보지 않는다. |
| Entity / Value Object | 식별자가 중요한 유닛·계정과 값 자체가 중요한 좌표·정의를 구분한다. 공유 정의를 개체 상태와 섞지 않는다. |
| Aggregate / Aggregate Root | 함께 일관성을 지켜야 하는 상태와 변경 진입점을 정한다. 월드 전체를 무조건 한 거대한 객체로 만들지 않는다. |
| Domain Service / Application Service | 개체 하나에 속하지 않는 규칙과 명령·저장 조정 절차를 구분한다. |
| Specification | 여러 곳에서 재사용하는 판정 조건을 표현한다. 단일 if를 위해 조건 클래스를 늘리지 않는다. |

도메인 용어는 [Microsoft의 DDD 도메인 모델 설명](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/microservice-domain-model), [Fowler의 응용 아키텍처 카탈로그](https://martinfowler.com/eaaCatalog/)를 참고한다. DDD는 설계 접근법이며 모든 항목이 GoF와 같은 분류의 패턴인 것은 아니다.
