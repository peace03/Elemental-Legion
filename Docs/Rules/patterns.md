# 디자인 패턴·아키텍처 선택 인덱스

객체 지향 패턴뿐 아니라 EventBus, UI 구조, 게임 루프, 저장·메시징·운영·네트워크 기법까지 검토한다. 서로 다른 수준의 개념이므로 모두 GoF 패턴이라고 부르지는 않는다. 이 목록은 선택지이며 사용 가능한 패턴을 제한하거나 전부 구현하라는 지시가 아니다.

## 선택과 읽기 순서

1. 현재 문제가 생성·구조·협력·표시·상태·저장·부하 중 무엇인지 정하고, 기존 구현과 설치된 기능부터 확인한다.
2. 아래 표에서 해당 분야만 열어 관련 절을 읽는다. 모든 분야 문서를 매번 읽지 않는다.
3. 단순한 데이터·함수·조건문·컴포넌트 조합과 비교해 현재 요구의 변경 범위와 복잡도를 줄이는 방식을 선택한다. 수치 차이는 데이터로 처리한다.
4. 채택 이유가 코드에서 드러나지 않으면 한글 주석으로 설명한다. 의존성·구독 수명·상태와 자원의 소유권·할당 비용을 확인한다.
5. 서버 권위·시야 차단·명시적인 틱 순서·공유 코드의 Unity 비의존성을 유지한다. 새 패턴을 이유로 프레임워크·분산 서비스·데이터베이스를 임의로 추가하지 않는다.

## 분야별 인덱스

| 현재 설계 문제 | 읽을 문서와 주요 항목 |
| --- | --- |
| 객체 생성·조합·알고리즘 협력 | [GoF 23개](Patterns/gof.md): Factory, Builder, Adapter, Decorator, Facade, State, Strategy, Visitor 등 |
| HUD·메뉴·표시 상태·화면 테스트 | [UI·프레젠테이션](Patterns/presentation.md): MVC, MVP, MVVM, MVI, Passive View, Presentation Model, 단방향 데이터 흐름 |
| 모듈 알림·이벤트 전달·메시지 처리 | [EventBus·메시징](Patterns/messaging.md): Event Aggregator, Pub/Sub, Event Queue, Reactive, 라우팅·변환·전달 보장 |
| 계층·모듈 경계·의존성·도메인 구조 | [아키텍처·DI](Patterns/structure.md): Layered, Hexagonal, Clean, Onion, DI, Actor, DDD 등 |
| 틱·많은 유닛·데이터 배치·봇 행동 | [게임·AI](Patterns/game.md): Game Loop, Double Buffer, Component, ECS, Pool, Spatial Partition, FSM/HFSM, BT, Utility AI, GOAP |
| 파일·계정·월드 저장·조회·이력 | [데이터·저장](Patterns/data.md): Repository, Unit of Work, Mapper, DTO, CQRS, Event Sourcing, Outbox/Inbox, Cache |
| 비동기 I/O·외부 장애·과부하·배포 | [복원력·운영](Patterns/resilience.md): Bounded Queue, Backpressure, Retry, Circuit Breaker, Saga, 운영 패턴 탐색 목록 |
| 예측·보정·보간·스냅샷·대역폭 | [실시간 네트워크](Patterns/networking.md): Prediction, Reconciliation, Interpolation, AOI, Delta, Quantization, Lockstep/Rollback 비교 |

## 조사 범위와 확장

2026-11-02에 Unity·Microsoft 공식 문서, Fowler와 각 분야 원저자·구현 프로젝트 자료를 조사했다. 분야별 문서의 원문 링크에서 필요한 정의와 세부 조건을 확인한다. 프로젝트 적용 예와 금지 조건은 이 저장소의 판단이며 원저자의 일괄 권고를 뜻하지 않는다.

위 인덱스에서 답을 찾지 못하면 해당 분야의 원문 카탈로그를 추가 조사한다. 보편적으로 닫힌 '모든 패턴' 목록은 없으므로, 필요한 새 패턴을 찾으면 관련 분야 한 곳에 출처·역할·적용 조건을 보완한다. 긴 목록을 루트 AGENTS.md나 공통 코딩 규칙에 복제하지 않는다.
