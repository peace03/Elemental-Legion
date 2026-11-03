# 관측소 UI 구현 계획

> 구현은 승인된 설계와 검증 단위에 맞춰 순서대로 진행한다.

관측소 HUD, 미니맵, 서버 집결 상태, 씬 전환과 설정을 Unity 6000.6 uGUI 및 기존 Input System에 연결한다. 전투 세션은 기존 `ClientBootstrap`이 소유하고, 씬 조정은 `ClientFlow`가 담당한다. [승인된 설계](2026-11-03-m3-feedback-design.md)

- [x] `VisibilitySystem`에서 자기 집결 상태와 실제 합류 누적 수를 뷰에 노출하고 `GameplayCodec`의 v4 본문에 검증된 필드를 추가한다. `M3GameplayTests`에서 명령 전·후, 소유자 격리, 수비대가 없는 경우와 비정상 플래그를 검사한다.
- [x] `FogPresentation`의 월드 모드를 장식 연무로 연결한다. `ObservatoryMap`은 기존 탐색 마스크·타임라인을 이용해 시야와 아이콘을 표시한다. 기존 `CombatHud`의 양성·집결 이벤트를 재사용하고 집결 sequence를 추적한다.
- [x] `ControlBindings`의 열 개 키 슬롯과 `ClientPreferences`를 구현한다. 기존 이동 액션의 키보드 파트만 덮어쓰고 게임패드 바인딩을 유지한다. 키 충돌·복원과 잘못된 저장값은 관리 코드로 확인하고 설정 중 입력 차단은 Unity Play에서 확인한다.
- [x] `ClientFlow`, `ClientScreen`, `ClientSettingsScreen`을 연결하고 Bootstrap·Title·ServerBrowser·Settings·PlayerDashboard 씬을 Build Settings에 등록한다. 설정·현황은 추가 로드한다. 취소 후 늦게 연결된 세션을 정리하고, 접속 실패에는 주소와 선택을 보존한다.
- [x] 기존 .NET 빌드와 전체 테스트를 실행한다. Unity 프로젝트 컴파일러로 런타임·Editor·EditMode 코드를 컴파일하고 씬 참조를 확인한다.
- [ ] Unity에서 Bootstrap을 열고 접속, 탐색과 집결, 키 변경 후 복귀, 설정 취소와 해상도 복구를 확인한다. 두 클라이언트의 교전과 Windows Player 화면은 별도로 확인한다.

## 확인 결과

- .NET Release 솔루션 빌드에서 경고와 오류가 0개였고, 테스트 150개가 통과했다. 집결 플래그의 소유자 격리와 패킷 검증, 실제 수비대 합류를 포함한다.
- Unity 6000.6의 컴파일러와 프로젝트 참조로 Shared, Client, Editor, EditModeTests를 컴파일했다. 첫 씬, 활성 씬 목록, Bootstrap GUID와 신규 씬의 로컬 참조도 확인했다.
- 컴파일된 키 설정 코드에서 유효한 저장값, 중복 입력 차단, Enter 예약, 유효하지 않은 저장값의 원자 처리, 기본값 복원을 실행했다.
- 독립 검토에서 발견한 Enter 중복 입력, 동기 DNS, 단독 Play의 화면 설정 및 현황 수명, 인구로 추정하던 합류 완료를 수정했다. 입장 응답에도 10초 제한을 적용했다.
- Unity Editor의 실제 씬 가져오기, EditMode 테스트 실행, Play, 셰이더 출력, Windows Player 빌드는 이 확인에 포함하지 않았다.

## 건물 우회와 추가 집결

2026-11-03에 [Reynolds의 장애물 회피 설계](https://www.red3d.com/cwr/papers/1999/gdc99steer.html)와 [Recast Navigation](https://github.com/recastnavigation/recastnavigation)을 확인했다. 서버의 원형 건물에는 기존 선분 충돌과 접선 이동을 적용한다. Recast의 탐색 메시와 Unity 탐색 에셋은 도입하지 않았으며 HUD는 기존 uGUI를 재사용한다.

서버는 한 번의 집결 명령에 당시 수비대만 포함하고, 이동 중에도 다음 명령을 접수한다. v5의 집결 인원과 전체 수비대 인원으로 버튼 아래의 이동 중·본진 대기 수를 갱신한다.

.NET Release 빌드와 테스트 157개, Unity 6000.6 컴파일러를 사용한 네 어셈블리의 컴파일이 통과했다. Unity Play의 이동과 HUD 배치는 확인하지 않았다.

동일 PC에서 Release·시드 1·워밍업 1,000틱·측정 10,000틱으로 비교했다. 두 최대 부하의 표본은 750기와 중립 238기로 시작했다.

| 시나리오 | 틱 p99 전 → 후 | 진형 처리 평균 전 → 후 | 틱당 GC 할당 전 → 후 |
| --- | ---: | ---: | ---: |
| 분산 | 1.343 → 1.448ms | 0.202 → 0.282ms | 1292.3 → 1290.0B |
| 밀집 | 7.853 → 7.134ms | 0.227 → 0.303ms | 8665.6 → 8664.0B |

## 원소 사이 간격

2026-11-03에 [Reynolds의 무리 움직임과 간격 유지](https://www.red3d.com/cwr/steer/gdc99/index.html)와 [RVO2의 상호 회피](https://gamma-web.iacs.umd.edu/RVO2/)를 검토했다. 플레이어별 유닛 목록과 기존 건물 충돌을 사용해 아군 쌍만 보정한다. 공간 격자는 보정된 위치로 전투와 시야를 계산한다.

목표 간격은 임시값 1.35m다. Unity의 전장 몸체, 소유자 링, 몸체 입자는 기본 가로 반경 0.6m 안에 맞춘다. 집결 전 수비대의 새 양성 위치는 본진 주변의 빈 슬롯을 먼저 쓴다.

.NET Release 빌드, 테스트 161개, Unity 컴파일러를 사용한 네 어셈블리의 컴파일이 통과했다. Unity Play에서 원소의 가독성과 움직임을 확인하는 작업은 남아 있다.

15명 표준 부하 10,000틱에서 분산 평균 1.771ms와 p99 3.585ms, 밀집 평균 8.527ms와 p99 12.868ms를 기록했다. 틱당 할당은 각각 1284.2B와 9435.0B였다. 다른 게임이 실행 중인 상태에서 워밍업 300틱과 측정 1,000틱으로 직전 빌드와 비교했으며 밀집 p99는 12.845ms에서 12.908ms로 변했다. 두 빌드 모두 이 환경에서 10ms 기준을 넘었다.

## 군단 중심 마커

군단의 이동과 건물 충돌을 예측하는 바닥 좌표에 기본 지름 0.6m의 흰 점을 표시한다. 원소 몸체 위에서도 점을 읽을 수 있게 기존 마커의 원형 셰이더를 깊이 검사 없이 그린다. 표시 크기는 `LegionPresentation` 인스펙터에서 조정한다.

2026-11-03에 [Unity 파티클 API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ParticleSystem.SetParticles.html)와 기존 Kenney 파티클 텍스처를 확인했다. 기존 월드 공간 파티클과 프리팹 연결을 재사용하고 단색 점은 셰이더로 그린다.

## 뒤처진 원소의 복귀

2026-11-03에 [Reynolds의 도착과 선두 추종](https://www.red3d.com/cwr/steer/gdc99/index.html)과 [OpenSteer의 조향 구현](https://github.com/meshula/OpenSteer)에서 목표 접근 방식을 확인했다. 서버의 기존 슬롯 이동과 건물 충돌을 재사용하고, 아군 간격 보정에도 같은 틱 이동 한도를 적용한다.

중심과의 발동 거리 8m, 복귀 속도 1.6배를 임시값으로 등록했다. 진형 복귀를 시작한 원소는 중심과의 거리가 다시 8m 아래로 줄어도 자기 자리에 닿을 때까지 따라잡는다.
