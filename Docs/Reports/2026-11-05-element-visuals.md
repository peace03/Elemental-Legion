# 원소 14종 표현 개선 결과

원소 갤러리와 전투 시안을 참고해 14종 모두 본체와 대기·이동·공격·피격·전향·소멸 표현을 개선했다. 사용자가 본체와 전투 동작까지 함께 개선하는 범위를 확인한 뒤 작업했다. 게임 밸런스·이동 판정·피해·시야·네트워크 프로토콜은 변경하지 않았다.

## 적용 내용

| 원소 | 본체·표현 |
| --- | --- |
| 물 | 큰 물방울과 위성 물방울, 굴절·표면 흐름·물방울 입자 |
| 불 | 휘는 불꽃과 불꽃 조각, 밝은 핵과 주황색 외곽, 상승 입자 |
| 바람 | 여러 나선이 감기는 회오리와 흐르는 밝은 띠 |
| 연기 | 겹쳐 흐르는 구름 덩어리와 위로 퍼지는 연무 |
| 얼음 | 방사형 얼음 파편과 차가운 하이라이트 |
| 흙 | 공중에 분리된 바위 조각과 낮게 흩어지는 먼지 |
| 번개 | 여러 갈래로 갈라지는 번개와 빠른 스파크 |
| 빛 | 중심 광원, 방사형 빛살과 금색 광륜 |
| 결정 | 중심 결정과 작은 위성 결정, 회전·반짝임 |
| 붕괴 | 녹색 붕괴 핵, 주변 입자와 불규칙한 표면 |
| 암흑 물질 | 검은 중심과 보라색 강착 고리 |
| 중력 | 높이가 다른 궤도와 공전하는 작은 입자 |
| 자기장 | 청록색·보라색으로 엮이는 자기장 고리 |
| 생명 | 휘는 잎과 씨앗, 상승하는 생명 입자 |

각 본체에 개체별 위상이 다른 부유·맥동·회전을 적용하고, 이동 방향·속도에 따라 자세와 흔적을 바꾼다. 공격 때는 본체에 반동을 주고 실제로 시작점에서 목표로 이동하는 입자와 원소 메시를 표시한다. 피격은 짧은 수축과 파편, 전향은 중심으로 응집하는 입자와 본체 반응, 소멸은 원소 재질에 맞는 파편 방출을 사용한다. 공격 효과는 이미 서버가 확정한 사건의 표현이며 도착 시 피해를 다시 판정하지 않는다.

몸체와 본체 주변의 지속 입자는 기존 표시 반경 안에 맞춘다. 이동 흔적·공격·소멸의 일회 효과는 동작을 보여 주기 위해 몸체 밖으로 퍼질 수 있다. 새 GameObject나 Update를 유닛마다 만들지 않고 기존 인스턴스 렌더링, 원소별 Particle System과 제한된 일회 효과 풀을 사용한다. 시야에서 잊힌 원소의 동작 상태와 해당 원소를 시작점·목표로 삼은 효과를 정리한다.

`ElementArtProfile`에서 부유·맥동·회전·반동·효과 속도·입자 수·변형 여백을 조절할 수 있다. `ElementalVisuals`의 원소 발광 항목에서 빛 번짐 강도·임계 밝기·확산을 조절한다. 발광 강도 0으로 초기화하면 추가 후처리를 만들지 않는다. 카메라의 기존 설정은 보존하며 직접 추가한 카메라 컴포넌트와 임시 VolumeProfile은 종료 때 정리한다.

## 자산과 변경 범위

- `Meshes/Elements/{id}.asset`: 단일 submesh의 고유 메시 14개, 원소당 360~1,864 정점.
- `Materials/Elements/{id}.mat`, `Settings/Elements/{id}.asset`: 기존 자산 GUID·ID·입자 재질 참조를 보존하고 원소의 메시·색·표현 설정을 갱신했다.
- `ElementVisualArtSetup.Configure()` 및 `Elemental Legion/Configure Element Visual Art` 메뉴: 해당 메시·재질·프로필만 생성·저장한다. 반복 실행에서 메시 GUID와 프로필 ID/style/입자 참조가 유지됨을 확인했다.
- `ElementBody.shader`, `ElementalVisuals.cs`, `ElementalVisuals.Motion.cs`, `ElementalVisuals.Glow.cs`: 표면, 동작과 효과, 카메라 발광의 소유·정리를 담당한다.
- `LegionClient.prefab`와 `ElementGallery.unity`: 자기권이 물 원소의 새 물방울 메시를 확대하지 않도록 기존 `ElementSphere.asset` 참조 한 줄만 추가했다. `M3SceneSetup`의 기존 구성 경로에도 이 참조를 추가했다.

설치된 Unity Technologies Particle Pack과 CC0 Kenney Particle Pack의 기존 텍스처를 재사용했다. 라이선스·고지·원본 경로는 [기존 M3 기록](../third-party.md)에 있다. 새로운 외부 패키지·유료 에셋은 추가하지 않았고 원소 메시·셰이더는 프로젝트 코드로 제작했다.

## 검증

| 검증 | 결과 | 근거 |
| --- | --- | --- |
| Unity 컴파일 | 오류 0, 경고 0 | [컴파일 결과](../../.artifacts/element-visuals/compile-final.json) |
| Unity EditMode 전체 | 40/40 통과 | [테스트 결과](../../.artifacts/element-visuals/tests-final.json) |
| .NET 솔루션 빌드 | 오류 0, 경고 0 | [빌드 로그](../../.artifacts/element-visuals/dotnet-build.log) |
| .NET 전체 테스트 | 322/322 통과 | [테스트 로그](../../.artifacts/element-visuals/dotnet-test.log) |
| 아트 재구성 | 14종 연결, 반복 실행 GUID·입자·ID 보존 | [구성 결과](../../.artifacts/element-visuals/art-setup.json) |
| 988기·1080p HDR 렌더링 | 동기화·읽기 비용 포함 처리 p95 14.26ms, GPU 표본 p95 7.80ms | [최종 표본](../../.artifacts/element-visuals/final-dense-synchronized.json) |

새 테스트는 고유 실루엣·유효한 정점/UV/법선/색상, 시간에 따른 본체 변화, 밀집 상태의 몸체 반경, 자기권의 별도 구형 메시, 이동하는 공격과 시작 원소 Forget, 발광 카메라의 기존 설정 복원·추가 컴포넌트 제거를 확인한다. 기존 구현에서 실패를 확인한 뒤 수정했다. 독립 코드 리뷰에서 찾은 자기권 메시 공유와 카메라 컴포넌트 정리 누락도 반영했다. 새 자산의 메타 파일 누락은 없다.

28기 전투 표본은 100틱 동안 공격 119회·사망 16회·전향 6회를 발생시켰고 장식 효과 생략은 0회였다. 실제 서버 시뮬레이션·시야 복제·사건 표시 경로를 사용한 임시 씬 표본이며 온라인 실전 검증은 아니다. 검증 종료 후 임시 씬은 닫혔고 원래 Bootstrap 씬 한 개·수정 없음·Play 꺼짐 상태를 확인했다. [편집기 최종 상태](../../.artifacts/element-visuals/editor-final.json)

표본 환경은 RTX 3060 Ti / Direct3D11 / 1920×1080이며, 300회 중 초기 60회를 제외한 240개 표본이다. 처리 시간은 클라이언트 표현·카메라 렌더링·GPU 완료를 기다리는 1픽셀 읽기를 포함하므로 순수 CPU 시간이나 Player 프레임 시간과 같지 않다. GPU 지표도 Editor 카메라 표본이며 실제 게임 전체 프레임 수용 결과가 아니다. 입자 수는 최종 밀집 표본 6,776개, VFX 풀 최고 사용량은 192/256이었다. 인위적으로 988기에 일회 방출을 동시에 반복한 조건에서 장식 효과 3,980회가 상한 때문에 생략됐으며 중요 효과 예약은 유지한다. 0회 생략이나 무제한 효과를 보장하지 않는다.

개선 전 기준 화면·표본은 `before-gallery.*`, `before-dense.*`에 있다. 초기 오프스크린 도구가 사용하던 SingleCameraRequest는 정상 카메라의 Volume 갱신 경로와 달랐고 GPU 요청 완료를 기다리지 않아, 일부 진단 표본에 약 100ms의 일관되지 않은 GPU 지표가 나타났다. 추가 카메라 데이터 제거·빛 번짐 비활성·몸체 입자 비활성 대조를 수행한 뒤 StandardRequest, HDR 타깃, GPU 완료 읽기와 화면 표시 색 변환을 사용하도록 검증 도구를 보완했다. 이전과 최종 측정 방식이 달라 수치만으로 성능 향상을 주장하지 않는다. GPU Recorder 값은 프레임 내 누적값이므로 블록 수와 호출당 평균도 함께 기록했다. [Unity Recorder API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Profiling.Recorder.html)

## 실제 렌더링 확인 자료

- [원소 14종 확대 비교](../../.artifacts/element-visuals/element-showcase.png): Unity에서 직접 렌더링한 본체, 실제 소유권 링과 이름.
- [갤러리 첫 시점](../../.artifacts/element-visuals/final-gallery-synchronized.png), [다음 시점](../../.artifacts/element-visuals/final-gallery-synchronized.second.png).
- [전투 표본](../../.artifacts/element-visuals/final-combat-synchronized.json), [전투 첫 시점](../../.artifacts/element-visuals/final-combat-synchronized.png), [다음 시점](../../.artifacts/element-visuals/final-combat-synchronized.second.png).

확대 비교는 `.artifacts/element-visuals/capture-contact.cs`로 재현한다. `M3RenderProbe`는 임시 추가 씬의 로컬 시뮬레이션과 실제 렌더링 코드를 사용하며 네트워크 접속이나 Play 진입은 수행하지 않는다.

사람의 Play 조작·외부망 경기·Windows Player 전체 프레임과 메모리 측정은 수행하지 않았다. Editor의 개별 스레드 GC 카운터는 이 환경에서 지원되지 않아 무할당을 입증하지 않았으며, 전체 Editor 프레임의 GC 지표는 로그에 별도로 남겼다. 시설 모델·UI를 추가 제작하거나 게임 규칙을 바꾸지 않았고, 다른 작업의 변경을 보존했다. 커밋·푸시는 하지 않았다.
