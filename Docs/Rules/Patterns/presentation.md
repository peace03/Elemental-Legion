# UI와 프레젠테이션 패턴

UI 구조는 [Unity MVC·MVP 가이드](https://learn.unity.com/tutorial/build-a-modular-codebase-with-mvc-and-mvp-programming-patterns)와 [Fowler의 GUI 아키텍처 분류](https://martinfowler.com/eaaDev/uiArchs.html)를 참고한다. 아래 적용 예와 제한은 이 프로젝트의 기준이다.

| 패턴·구조 | 역할과 적용 기준 |
| --- | --- |
| MVC | Model은 데이터·도메인 동작, View는 표시, Controller는 입력 조정을 맡는다. 구현 변형이 많으므로 실제 참조 방향을 명시한다. 클라이언트 Controller에 서버 게임 판정을 넣지 않는다. |
| MVP | Presenter가 View 인터페이스와 표시 데이터를 조정한다. Unity 없이 화면 동작을 테스트해야 하는 HUD·메뉴에서 검토한다. |
| Passive View | View는 표시와 입력 전달에 집중하고 Presenter가 갱신을 담당한다. 모든 단순 텍스트까지 복잡한 추상화로 감싸지 않는다. |
| Supervising Controller | 단순 데이터 연결은 View에, 복잡한 표시 조정은 Controller에 둔다. 두 곳에 같은 조건을 복제하지 않는다. |
| Presentation Model | 선택 상태·버튼 활성 여부 등 화면 상태를 위젯에서 분리한다. 서버 월드 상태와 화면 상태를 구분한다. |
| MVVM | ViewModel이 표시 값과 명령을 제공하고 View와 바인딩한다. 사용 중인 Unity UI 프레임워크의 바인딩 기능과 비용을 먼저 확인한다. |
| Flux / Redux식 단방향 데이터 흐름 | Action → 상태 갱신 → View 표시의 흐름을 명확히 한다. UI 상태가 복잡할 때 검토하며, 게임 월드를 매 프레임 복사하는 전역 Store를 기본으로 만들지 않는다. |
| MVI(Model–View–Intent) | 사용자 입력의 의도를 Intent로 해석하고 Model 상태를 View로 표시한다. 이벤트 스트림을 조합하는 화면에서 검토하며 서버 명령과 화면의 의도를 구분한다. |
| Application Controller | 화면 전환과 사용 흐름을 조정한다. 전투·경제·네트워크 구현까지 한 관리자에 합치지 않는다. |
| Humble Object | 테스트하기 어려운 Unity 위젯·수명주기 코드를 얇게 두고 판단을 일반 C# 코드로 분리한다. |

Passive View와 Supervising Controller의 구분은 [원저자 설명](https://martinfowler.com/eaaDev/PassiveScreen.html), [Supervising Controller](https://martinfowler.com/eaaDev/SupervisingPresenter.html)를 따른다. Presentation Model과 MVVM은 관련 개념이며 별도 계층을 둘 다 만들어야 한다는 뜻이 아니다. [Presentation Model](https://martinfowler.com/eaaDev/PresentationModel.html), [Microsoft MVVM](https://learn.microsoft.com/en-us/dotnet/architecture/maui/mvvm)을 참고한다. Flux/Redux 항목은 [공식 데이터 흐름 설명](https://redux.js.org/tutorials/fundamentals/part-2-concepts-data-flow)을 참고한 선택지이며 JavaScript 라이브러리 도입 지시가 아니다.

MVI는 [Cycle.js의 공식 설명](https://cycle.js.org/model-view-intent.html)과 [André Staltz의 단방향 UI 구조 비교](https://staltz.com/unidirectional-user-interface-architectures.html)를 참고한다. 같은 약어에도 변형이 있으므로 실제 상태 변경·입력·렌더링 경로를 명시한다.

## 적용 규칙

- 기존 UI 구조와 실제 복잡도를 기준으로 선택한다. MVC·MVP·MVVM을 한 화면에 겹쳐 넣지 않는다.
- 클라이언트 Model/ViewModel은 수신한 정보와 화면 상태만 다룬다. 시야 밖 데이터 복원이나 서버 판정 복제는 하지 않는다.
- 구독·바인딩은 화면 수명에 맞춰 해제하고, 씬 전환 뒤 늦게 온 응답이 이전 화면을 갱신하지 않게 한다.
