# 게임 루프·데이터·AI 패턴

게임 프로그래밍 분류는 원저자 Robert Nystrom의 [Game Programming Patterns 목차](https://gameprogrammingpatterns.com/contents.html)를 조사했다. 아래는 각 이름을 이 프로젝트에서 검토할 지점에 연결한 규칙이다. Command·Flyweight·Observer·Prototype·Singleton·State는 [GoF 문서](gof.md)에서 읽는다.

| 패턴·기법 | 역할과 프로젝트 기준 |
| --- | --- |
| Game Loop / Fixed Timestep | 서버를 고정 틱으로 진행한다. 실제 틱 순서와 주기는 기존 아키텍처·JSON을 따른다. |
| Update Method | 같은 갱신 계약으로 개체를 처리한다. Unity 개체마다 별도 Update를 추가할 필요가 있는지 확인한다. |
| Double Buffer | 현재 읽을 상태와 다음에 반영할 상태를 분리한다. 도입으로 전투·사망의 적용 시점이 달라지지 않게 한다. |
| Bytecode | 제한된 실행 규칙을 명령 형태로 해석한다. 현재 JSON 수치에 새 가상 머신을 붙이지 않는다. |
| Subclass Sandbox | 공통 기반 동작을 제한된 연산으로 확장한다. 상속이 필요한 계약인지 먼저 확인한다. |
| Type Object | 개체의 종류별 속성을 별도 정의로 공유한다. 원소 종류와 개별 유닛 상태를 구분한다. |
| Component | 기능을 작은 역할의 조합으로 구성한다. 의존성·초기화·갱신 순서를 명시한다. |
| Event Queue | 처리를 지정된 시점으로 미룬다. [메시징 수명·순서 규칙](messaging.md)을 따른다. |
| Service Locator | 서비스 조회 방식과 위험은 [의존성 문서](structure.md)를 따른다. |
| Data Locality / SoA | 함께 처리하는 데이터를 연속 배치한다. 배열 배치 변경은 실제 부하에서 측정한다. |
| Dirty Flag | 변경된 데이터만 재계산·전송한다. 변경 표시와 초기화 시점 누락을 테스트한다. |
| Object Pool | 반복 생성되는 VFX·표현 오브젝트의 할당 비용을 줄일 때 사용한다. 반환 시 상태·이벤트·타이머를 초기화하고 중복 반환을 막는다. 보유량도 제한한다. |
| Spatial Partition | 공간 해시 등으로 적·시야 탐색 범위를 줄인다. 경계·이동·제거 시 인덱스 정합성을 확인한다. |
| ECS | Entity·Component 데이터와 이를 처리하는 System을 나눈다. 일반적인 컴포넌트 조합과 구분하며 Unity DOTS 도입이 필수는 아니다. |

ECS 개념은 [Unity Entities 설명](https://docs.unity3d.com/Packages/com.unity.entities@1.0/manual/concepts-intro.html)을 참고한다. 프로젝트의 `shared/`에 Unity 의존성을 추가하지 않는다.

## 봇과 행동 선택

| 패턴·기법 | 역할과 적용 기준 |
| --- | --- |
| FSM / HFSM | 상태와 전이를 명시하고, 복잡해지면 상위·하위 상태로 공통 전이를 묶는다. |
| Behavior Tree | 조건·작업을 Selector·Sequence 등의 트리로 조합한다. 실행 중 작업의 중단과 재시작을 정의한다. |
| Blackboard | AI가 관측한 정보를 명시적인 공유 데이터로 둔다. 전역 월드 수정 통로로 사용하지 않는다. |
| Utility AI | 후보 행동의 점수로 선택한다. 동률 처리·재평가 주기·시드 재현성을 정한다. |
| GOAP | 목표와 행동의 전제·효과로 실행 계획을 찾는다. 계획 탐색 비용과 실패 후 재계획을 고려한다. |

상태 계층과 행동 트리는 [Epic StateTree 개요](https://dev.epicgames.com/documentation/en-us/unreal-engine/overview-of-state-tree-in-unreal-engine), [Behavior Tree 설명](https://dev.epicgames.com/documentation/en-us/unreal-engine/behavior-tree-in-unreal-engine---quick-start-guide)을 참고한다. Utility AI는 [Mark·Dill의 GDC 발표](https://media.gdcvault.com/gdc10/slides/MarkDill_ImprovingAIUtilityTheory.pdf), GOAP는 [GOAP 구현 프로젝트의 동작 설명](https://goap.crashkonijn.com/readme/tutorial/gettingstarted)을 참고한다.

이 항목들은 확장 후보이며, 현재 봇은 중립 사냥 → 양성 → 적 접근의 단순 규칙부터 구현한다. 복잡한 AI는 별도 요구가 있을 때 적용한다.
