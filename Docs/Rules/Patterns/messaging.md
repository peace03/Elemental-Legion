# EventBus와 메시징 패턴

EventBus는 이벤트를 전달하는 통로, Event Aggregator는 여러 발행원의 이벤트를 모으는 역할, Event Queue는 처리 시점을 늦추는 버퍼다. 이름만으로 동기·비동기, 처리 순서, 스레드 안전성이나 전달 보장이 결정되지는 않는다. [Event Aggregator 원문](https://martinfowler.com/eaaDev/EventAggregator.html), [Message Bus 원문](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessageBus.html)을 참고한다.

## 프로세스 내부의 알림과 흐름

| 패턴·기법 | 역할과 프로젝트 기준 |
| --- | --- |
| EventBus / Event Aggregator | 월드·화면 등 명시적인 범위 안에서 타입이 있는 이벤트를 중계한다. 모든 기능이 접근하는 전역 static 버스를 기본으로 두지 않는다. |
| Observer | 발행원과 구독자가 직접 알림 계약을 맺는다. 단순한 일대다 알림은 C# event 등 기존 기능부터 검토한다. |
| Publish–Subscribe | 발행자와 수신자를 채널로 분리한다. 로컬 알림인지 프로세스 간 전송인지 구분한다. |
| Event Queue / Command Queue | 결과 알림이나 요청을 나중에 처리한다. 처리할 틱·순서·용량·오래된 항목 정책을 정의한다. |
| Mediator | 여러 객체의 협력 절차를 조정한다. 이벤트를 단순 전달하는 버스와 역할을 구분한다. |
| Domain Event | 도메인에서 이미 일어난 사실을 표현한다. 서버가 확정한 결과만 표현 계층으로 내보낸다. |
| Integration Event | 프로세스 경계를 넘는 알림이다. 내부 객체를 그대로 내보내지 않고 버전이 있는 전송 계약을 둔다. |
| Reactive Streams / Observable | 시간에 따른 값을 조합·필터링한다. 구독 수명, 실행 스레드, 오류와 역압 정책을 확인한다. 별도 라이브러리는 필요성을 검증한 뒤 도입한다. |
| ScriptableObject Event Channel | Unity 자산을 통해 표현용 이벤트 연결을 편집한다. `shared/`에는 넣지 않으며, 자산에 월드별 구독 상태가 남지 않게 한다. |

Reactive 항목은 [ReactiveX의 Observable 설명](https://reactivex.io/documentation/observable.html)을 참고한다. ScriptableObject 채널은 [Unity의 공식 Event Channel 가이드](https://unity.com/how-to/scriptableobjects-event-channels-game-code)를 참고한 표현 계층의 구현 방식이며 별도의 네트워크 프로토콜을 뜻하지 않는다.

## 메시지 구성과 전달

메시징 패턴은 [Enterprise Integration Patterns의 구성 분류](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessageConstructionIntro.html)와 [채널 분류](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessagingChannelsIntro.html)를 기준으로 검토한다.

| 패턴 | 검토할 상황 |
| --- | --- |
| Command Message / Event Message / Document Message | 요청·발생 사실·데이터 전달의 의미를 구분한다. |
| Request–Reply / Correlation Identifier / Return Address | 비동기 응답을 원래 요청·세션에 연결한다. |
| Message Sequence / Expiration / Format Indicator | 분할 순서·유효기간·형식 버전을 정의한다. |
| Point-to-Point / Publish–Subscribe Channel / Datatype Channel | 단일 처리자·여러 구독자·메시지 종류별 경로를 나눈다. |
| Invalid Message / Dead Letter Channel | 잘못되거나 계속 실패하는 메시지를 정상 흐름에서 분리한다. |
| Guaranteed Delivery | 필요한 전달 보장을 구현·검증한다. 로컬 버스나 단순 큐에 자동으로 보장된다고 가정하지 않는다. |
| Channel Adapter / Messaging Bridge | 외부 전송 시스템을 연결한다. 이미 LiteNetLib가 제공하는 기능을 중복 구현하지 않는다. |

## 라우팅·변환·처리

아래는 [라우팅](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessageRoutingIntro.html), [변환](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessageTransformationIntro.html), [처리기](https://www.enterpriseintegrationpatterns.com/patterns/messaging/MessagingEndpointsIntro.html) 분류의 탐색 항목이다. 묶음 안에서도 필요한 패턴만 선택한다.

| 패턴 | 검토할 상황 |
| --- | --- |
| Pipes and Filters / Message Router / Content-Based Router | 처리를 단계로 나누거나 메시지 내용에 따라 보낼 곳을 정한다. |
| Message Filter / Recipient List / Dynamic Router | 수신자 선택과 전달 조건을 적용한다. 시야 차단은 서버에서 먼저 수행한다. |
| Splitter / Aggregator / Resequencer | 메시지를 나누거나 합치고 순서를 복원한다. 버퍼와 만료 조건이 필요하다. |
| Scatter–Gather / Routing Slip / Process Manager / Composed Message Processor | 여러 처리 단계·응답을 조정한다. 현재 단일 서버에 분산 워크플로를 미리 넣지 않는다. |
| Message Translator / Envelope Wrapper / Normalizer / Canonical Data Model | 외부 형식과 내부 계약을 분리한다. |
| Content Enricher / Content Filter / Claim Check | 필요한 데이터만 보충·제거하거나 큰 본문을 별도 보관한다. |
| Message Endpoint / Messaging Gateway / Messaging Mapper / Message Broker | 발행·수신 경계와 전송 구현을 분리한다. |
| Polling Consumer / Event-Driven Consumer / Selective Consumer | 폴링·알림·선별 수신 중 실행 환경에 맞는 방식을 고른다. |
| Competing Consumers / Message Dispatcher / Service Activator | 작업을 분배한다. 시뮬레이션의 상태 변경은 병렬 소비하지 않는다. |
| Transactional Client / Durable Subscriber / Idempotent Receiver | 트랜잭션·재접속·중복 처리에 필요한 보장을 정한다. |

운영 진단에는 Control Bus, Detour, Wire Tap, Message History, Message Store, Smart Proxy, Test Message, Channel Purger도 검토할 수 있다. 각 역할은 [시스템 관리 패턴 원문](https://www.enterpriseintegrationpatterns.com/patterns/messaging/SystemManagementIntro.html)을 확인한다. 메시지 추적 로그에 비밀값이나 불필요한 개인정보를 남기지 않는다.

## EventBus 도입 시 필수 확인

- 발행 범위, 구독자 소유권, 해제 시점, 중복 구독 정책을 정한다.
- 동기·지연 전달, 재진입, 발행 중 구독 변경, 구독자 예외의 처리 규칙을 정한다. 구독 등록 순서에 게임 판정을 의존시키지 않는다.
- 이벤트에는 변경 가능한 월드 객체를 무분별하게 노출하지 않는다. Unity 오브젝트 갱신은 메인 스레드에서 수행한다.
- 핵심 틱 순서는 명시적인 호출로 유지한다. 버스를 도입한다는 이유로 전투·사망·전향·시야 처리 순서를 바꾸지 않는다.
