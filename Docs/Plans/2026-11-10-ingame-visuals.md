# 인게임 목표 시안 적용 계획

사용자가 승인한 `Docs/Art/Concepts/2026-11-10-ingame-target/02-aaa-ingame-user-models-v2.png`를 실제 Unity 표시로 옮긴다. 시설에는 제공한 모델 5종을 사용하고 원소 14종의 본체는 입자와 셰이더·3D 메시로 표현한다. 기존 UI·소유색·시야·분대와 이동 보정은 유지한다.

## 범위와 완료 조건

- 시설 원시핵·응집로·점화로·포집기·수호 위성을 실제 메시와 원래 텍스처로 표시한다. 원시핵의 빈 FBX는 동명 Blender 원본의 두 저폴리 메시를 별도 사본으로 내보낸다. 수호 위성 한 모델은 자연·에너지 타워 양쪽에서 사용한다. 자기권은 기존 방벽 표현을 유지한다.
- 건설 중·설치 미리보기·피해·유령·서버 관측 해제와 플레이어별 소유색이 새 모델에도 적용된다. 원본 파일은 변경하지 않는다.
- 물의 유체 덩어리와 물결, 화염과 불티, 연기의 밀도와 흐름, 얼음·암석·결정의 재질, 각 에너지 원소의 구분을 개선한다. 이미지 한 장을 본체로 쓰지 않고 기존 14개 입자 그룹과 입자 예산을 유지한다.
- 월드에는 실제 빛을 받는 암석 바닥과 절제한 발광·그림자를 적용한다. 기존 URP 시야 연무와 탐색 정보 공개 범위는 유지한다.
- 조절할 표현 값은 인스펙터에 노출한다. JSON 수치·게임 판정·프로토콜은 바꾸지 않는다. 새 유료 에셋과 패키지는 추가하지 않는다.

## 변경 경계와 순서

1. 그래픽 담당이 `Art/Facilities`에 모델·이미지 사본과 `manifest.json`을 준비한다. 메인이 `FacilityModelCatalog`, `FacilityArtSetup`, `Facility.shader`와 런타임 부분 클래스로 연결한다. 기존 `ElementalVisuals`의 관측된 건물 흐름에서만 그린다.
2. 원소 담당이 기존 `ElementBody`, `ElementMatterParticles`, `ElementVisualArtSetup`과 입자 배치를 개선한다. 물체의 위치 보간과 소유 링은 그대로 사용한다.
3. 환경 담당이 `BattlefieldPresentation`, `BattlefieldArtSetup`, `Battlefield.shader`를 만든다. 메인이 프리팹과 기존 바닥 경로를 연결해 중복 지면을 없앤다.
4. 메인이 대상 Editor 경로를 확인하고 자산을 임포트·구성한다. 기존 씬과 사용자 작업을 보존한다. 기본 Unity MCP는 다른 프로젝트를 가리키므로 Unity CLI의 명시적 `--project-path C:/Project/Elemental-Legion/client`만 사용한다.
5. 독립 검토자가 실제 카메라 캡처를 참고 렌더와 비교한다. 문제를 보정하고 컴파일·관련 EditMode 검사·입자 예산·원본 보존을 확인한다.

## 재사용 판단

제공 모델과 설치된 URP 17.6·VFX Graph·Particle System, 기존 표면 텍스처·입자 자료를 활용한다. Unity의 [조명 함수 설명](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/shaders/writing-custom-shaders-urp/use-built-in-shader-methods/lighting)과 [Graphics의 조명 라이브러리](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl)를 참고하며, 로컬 설치 버전의 실제 헤더를 기준으로 작성한다. 외부 코드를 새로 복사하거나 패키지를 도입하지 않는다. 원시핵 외 모델의 청록 인셋에는 기존 색에서 추출하는 셰이더 마스크를 적용해 금색 테두리·석재·보라 결정을 보존한다.

## 검증

- 시설 5개 매핑, 서브메시·재질·텍스처 참조, 정규화한 지면·반경·높이와 건설 미리보기의 동일 모델 사용을 검사한다.
- 현재 관측 해제 시 새 모델도 다음 프레임에서 사라지고 유령은 서버가 허용한 상태만 표시되는지 확인한다.
- 같은 카메라의 전후 캡처와 시설 사선·위쪽, 원소 대기·이동·교전 캡처를 남긴다. 생성 시안과 실제 렌더를 구분한다.
- Unity 컴파일과 관련 전체 EditMode 테스트를 실행한다. 이 변경은 서버/공유 코드를 변경하지 않으므로 .NET 전체 빌드는 요구하지 않는다.
- 오프스크린 Editor 렌더와 실제 Play·외부 인터넷 방·Player 성능은 구분해 기록한다. 기존 사용자 씬은 저장하지 않으며 자동 Play는 수행하지 않는다.

## 진행

- 시설 사본 5종과 이미지 34개를 준비했으며 네 FBX는 원본과 해시가 일치한다. 원시핵은 `.blend`의 2개 저폴리 메시를 별도 FBX로 내보냈다.
- 시설 5종·원소 14종·암석 지면·국소 조명과 후처리를 기존 실행 프리팹에 연결했다.
- 최종 편집 모드 170/170, 셰이더 5종 오류 0개, 실제 카메라의 대기·이동·전투 및 시설 상태 캡처를 확인했다. 생성 시안과 실시간 결과의 시각 차이, Play·외부 인터넷 방·GPU 검증의 경계는 [결과 보고](../Reports/2026-11-10-ingame-visuals.md)에 정리했다.
