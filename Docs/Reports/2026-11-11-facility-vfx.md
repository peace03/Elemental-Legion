# 시설 5종 연출 적용

2026-11-11. [승인한 시안](../Art/Concepts/2026-11-11-facility-vfx/facilities-ingame.png)을 `LegionClient` 전장 프리팹에 적용했다. 시설에 쓰는 발광 재질은 전장 카메라의 HDR Bloom과 함께 표시된다.

| 시설 | 적용한 연출 |
| --- | --- |
| 원시핵 | 외부 금속 구조 중앙에 회전하는 청록 면각구를 넣었다. 삼각 면과 발광 경계를 셰이더로 그린다. |
| 포집기 | 획득 확정 사건에서 핵의 위치와 수집 시설 ID를 읽는다. 핵의 남은 결정이 작아지는 동안 금빛 입자가 나뉘어 출발하며, 곡선을 따라 중앙에 들어가면 흡입구 재질이 밝아진다. |
| 응집로 | 삼각 프레임 안에 푸른 유동선 16줄을 넣었다. 셰이더가 밝은 부분을 각 선의 길이를 따라 이동시킨다. |
| 점화로 | 기존 중앙 수정의 보라색 발광과 금속 링 세 부품의 상하 운동을 연결했다. 링마다 위상을 다르게 하고 게임 카메라에서 구분되도록 높이 차를 조정했다. |
| 수호 위성 | 배치한 원소의 메시와 본체 입자를 중앙에 띄운다. 받침에 원소색을 더하고 투사체가 표시된 원소 높이에서 출발하도록 맞췄다. |

시설 상태 막대를 모델 위로 옮겨 결정과 배치 원소가 가려지지 않게 했다. 크기, 링의 간격과 주기, 입자 수, 발광색은 프리팹의 `ElementalVisuals` Inspector에서 조절할 수 있다. 포집 입자는 전역 입자 예산에 포함되며, 시설이 시야에서 해제되거나 관측을 초기화할 때 지운다. 수집 출처는 기존 패킷 필드로 전송하며, 수집과 전송 양쪽에서 관측된 포집기의 ID만 공개한다. [프로토콜](../protocol.md)

## 확인한 결과

- Unity 6000.6.0f1의 Direct3D 11 렌더에서 시설 셰이더 세 개의 오류가 0개였다. EditMode 175개와 서버 테스트 798개가 통과했다. .NET 빌드는 경고와 오류가 각각 0개였다.
- 서버 수집을 복제한 Editor 시험에서 핵 ID 43을 수집한 포집기의 ID 5가 수신 사건의 출처와 일치했다. 관측 해제 후 해당 입자와 예산이 즉시 비워졌다.
- 아래 캡처는 실전 프리팹과 전장 카메라를 사용한 Editor 렌더다. 42기와 시설 다섯 종을 배치했다. 포집기는 서버 건설 명령과 수집 판정을 거쳤고, 나머지 시설은 비교용 위치에 표시했다.

| 캡처 | 관찰한 내용 |
| --- | --- |
| [전장 HUD](Artifacts/facility-vfx/hud.png) | 기본 전장 각도에서 다섯 모델과 각 효과의 가독성 |
| [비스듬한 시점 A](Artifacts/facility-vfx/oblique.png), [시점 B](Artifacts/facility-vfx/oblique-b.png) | 같은 카메라에서 약 1.16초 차이로 찍은 점화로 링 세 개의 상대 높이 |
| [포집 시작](Artifacts/facility-vfx/intake-start.png), [포집 도착](Artifacts/facility-vfx/intake-arrival.png) | 흰 원소핵이 금빛 입자로 풀리고 포집기 중앙이 밝아지는 두 시점 |
| [불을 배치한 수호 위성](Artifacts/facility-vfx/fire-tower.png) | 번개와 다른 본체 입자 및 받침 발광 |

시설 효과에는 [Unity URP Bloom](https://docs.unity.com/en-us/engine/6000.0/manual/post-processing-and-full-screen-effects/urp/effect-list/bloom)과 기존 Particle System을 사용했다. [Unity VFX Graph 샘플](https://github.com/Unity-Technologies/VisualEffectGraph-Samples)의 흐름 표현은 참고 자료로만 검토했다. [검증 수치](Artifacts/facility-vfx/validation.json)

인터넷 방에서 포집 연출을 보려면 서버에도 수집 출처 변경이 반영되어야 한다. 실제 Play, Windows Player 빌드, GPU 프레임 시간은 이번 검증 범위 밖이다.

## 시설의 발광과 주변 조명 보정

2026-11-11 추가 요청에 따라 시설 5종의 광량을 실제 전장 카메라에서 비교하고 보정했다. 원시핵에는 80개 삼각 면의 법선에 따른 굴절과 내부 광경로의 지수 감쇠를 적용했다. 면마다 전달되는 광량을 달리하고 경계를 더 밝게 그린다. 원시핵과 점화로의 작은 수정에도 청록 발광을 적용하고, 점화로 중심 수정은 원본의 면 무늬와 윤곽이 밝아지도록 조정했다.

시설마다 광원의 색과 중심을 설정하고, 거리가 멀어질수록 광량을 줄인다. 금속의 거칠기와 표면 방향에 따른 반사는 설치된 URP의 `LightingPhysicallyBased`로 평가한다. 바닥에는 가장자리에서 밝기가 0이 되는 발광 원판을 GPU 인스턴싱으로 제출한다. 시설별 빛은 약 4.2초 주기로 은은하게 맥동하며, 포집 확정 시의 밝기는 대기 중 광량에 더해진다. 색과 바닥 번짐의 강도·범위, 맥동 주기는 `LegionClient`의 `ElementalVisuals`에서 조절하고, 원시핵의 면·경계·내부 광량은 `FacilityNucleus` 재질에서 조절한다.

시설의 광량을 전달하는 경로에는 `SetVector`를 사용한다. Linear 프로젝트에서 `SetColor`로 HDR 빨강 3을 전달하면 11.2116으로 변환되는 것을 Editor에서 확인했다. 이 변환이 포집 순간의 넓은 과노출을 만들었으므로, 선형 광량을 직접 전달하고 수정의 밝기도 다시 맞췄다. [Unity MaterialPropertyBlock.SetColor](https://docs.unity3d.com/cn/2023.2/ScriptReference/MaterialPropertyBlock.SetColor.html)

산란과 바닥의 광량은 해석적 근사로 표현한다. 내부 차폐를 생략하므로 겹친 부품의 밝기는 실제 광경로와 차이가 생길 수 있다. 원시핵의 면 구성은 규칙적인 80면 구조다. 광경로에 따른 흡수식은 PBRT의 투과 설명을 참고했고, 거리 제한은 Unity의 조명 감쇠 설계를 참고했다. [PBRT Transmittance](https://pbr-book.org/4ed/Volume_Scattering/Transmittance), [Unity CommonLighting](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonLighting.hlsl)

### 추가 검증

- Unity EditMode 전체 180개가 통과했다. 이 중 시설 검사 7개는 참조, 포집 수명, 다섯 시설의 정상·유령·건설·체력 0 상태와 선형 HDR 광량을 확인한다.
- 시설용 셰이더 4종의 실제 사용 후 컴파일 오류가 0개였다. PC URP와 카메라의 HDR, 후처리 자료, 볼륨 레이어와 Bloom override 연결을 확인했다. Bloom 강도 0.55, 임계값 0.85, 확산 0.55로 렌더링했다.
- 바닥 빛 513개와 1,024개를 실제 GPU에 제출하고 배치 경계의 픽셀을 읽었다. 첫·마지막 인스턴스의 색과 위치가 맞았다. 원판을 256개씩 묶으므로 카메라 하나에서 1,024개를 표시하는 데 추가 제출 4회가 필요하다.
- RTX 3060 Ti, Direct3D 11의 Editor에서 워밍업 8회 후 조건별 24회를 측정했다. `ElementalVisuals.Render`의 관리 힙 할당은 시설 5개·200개 조건 모두 0바이트였다. 시설 200개의 지역 조명을 끈 조건의 CPU 중앙값은 1.909ms, 켠 조건은 2.280ms였다. 이 측정 구간은 렌더 준비이며 GPU 처리 시간과 화면 전체 프레임 시간은 별도다.
- 생성한 시험 씬을 닫은 뒤 `Bootstrap.unity`만 열린 상태, 씬의 미저장 변경 없음, Play 꺼짐을 확인했다. 실제 Play, Windows Player 빌드와 GPU 프레임 시간은 미검증이다. [상세 수치와 카메라 조건](Artifacts/facility-vfx/lighting/validation.json)

| 최종 Editor 렌더 | 확인한 표현 |
| --- | --- |
| [전장 HUD](Artifacts/facility-vfx/lighting/hud.png) | 실제 전장 각도에서 시설 5종과 유닛 42기의 밝기 |
| [원시핵](Artifacts/facility-vfx/lighting/detail-nucleus.png) | 청록색 결정 면, 밝은 경계, 내부 광원과 작은 수정 |
| [점화로](Artifacts/facility-vfx/lighting/detail-ignition.png) | 수정의 면과 윤곽, 금속 고리와 받침에 닿는 보라빛 |
| [응집로](Artifacts/facility-vfx/lighting/detail-cohesion.png), [수호 위성](Artifacts/facility-vfx/lighting/detail-tower.png) | 유동선과 배치 원소의 색으로 비추는 프레임·받침·바닥 |
| [포집 대기](Artifacts/facility-vfx/lighting/intake-rest.png), [도착](Artifacts/facility-vfx/lighting/intake-arrival.png) | 대기 중 금빛과 도착 순간에 밝아지는 우물의 세부 형태 |
| [비스듬한 시점 A](Artifacts/facility-vfx/lighting/oblique.png), [시점 B](Artifacts/facility-vfx/lighting/oblique-b.png) | 약 3초 차이의 결정 회전과 세 고리의 상대 높이 |
