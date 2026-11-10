# 인게임 그래픽 적용 결과

2026-11-10. [승인한 시안](../Art/Concepts/2026-11-10-ingame-target/02-aaa-ingame-user-models-v2.png)을 기준으로 제공 시설 5종과 실시간 원소 표현을 클라이언트 프리팹에 연결했다. 원소 본체는 3D 메시·Particle System·URP 셰이더를 사용한다.

## 적용

- 원시핵·응집로·점화로·포집기·수호 위성의 실제 메시와 원래 텍스처를 가져왔다. 원시핵의 빈 FBX는 같은 폴더 Blender 원본의 저폴리 메시 2개로 사본을 만들었다. 네 FBX 사본은 원본과 해시가 일치하고 제공한 원본 5개는 변경되지 않았다.
- 시설 카탈로그는 각 부품의 지면·수평 중심·반경·높이를 기록한다. 같은 모델을 완공·건설·설치 미리보기·유령에 사용하고, 관측 해제 후에는 모델·링·상태 막대를 그리지 않는다. 자연·에너지 타워는 같은 수호 위성 모델을 쓰고 작은 속성 코어와 발사 높이를 실제 모델 높이에 맞춘다.
- 원시핵은 원본 팀 마스크를 사용하고 다른 시설은 청록 인셋에 소유색을 적용한다. 넓은 패널에는 원래 표면색을 남긴다. 청록·빨강 소유색과 설치 가능·불가 표시를 캡처로 확인했다.
- 물에는 두께 있는 물마루와 움직이는 노멀·투과·반사, 불에는 화염 입자와 불티, 연기에는 낮고 넓은 입자 덩어리를 적용했다. 얼음·암석·결정의 형태와 표면, 번개의 주 방전과 연결 분기, 빛·자기장·중력·암흑 물질·붕괴·생명의 핵과 흐름을 보강했다. 기존 반경 1m 상한, 밀집 시 반경 보정, 이동 보간·회전, 14개 입자 그룹을 유지한다.
- 암석 바닥과 부분적인 물기, 전용 주광·보조광, 원소 접지 그림자, ACES와 색 보정을 연결했다. 발광 원소는 현재 표시한 유닛에서 최대 4개 공유 점광원을 선택해 주변 바닥·시설을 비춘다. `Forget`·`Clear`·비활성화 시 소등한다.

재적용 메뉴는 **Elemental Legion → Apply Ingame Visual Target**이다. 현재 프리팹에는 필요한 카탈로그·재질·컴포넌트 연결이 저장되어 있다. 개별 원소 설정은 `ElementVisualArtSetup.Configure`, 시설은 `FacilityArtSetup.Configure`, 지면은 `BattlefieldArtSetup.Configure`에서 재생성한다. 게임 판정·밸런스·패킷·기존 HUD 기능은 이 작업에서 변경하지 않았다.

## 실제 렌더 근거

아래 그림은 Unity Editor의 실제 메시·입자·셰이더·HUD를 오프스크린으로 렌더한 제어 장면이다. `GameRules.ForRoom(2)`의 240m 맵, 1920×1080, 아군 28기·적 14기를 사용했다. 시설 외형을 나란히 검토하기 위해 받은 프레임의 건물 표시 자료를 5종 배치로 구성했다. 이동 장면은 이전 위치를 미리 넣어 같은 보간·동작 경로를 검사했고, 전투 장면은 `SimulationWorld.Step`에서 발생한 17개 사건을 `GameplayReplicationState.Capture → 코덱 → GameplayReplica`로 전달해 재생했다.

| 화면 | 확인한 내용 |
| --- | --- |
| [HUD](Artifacts/ingame-visuals/hud.png) | 시설 5종, 분대·양성 UI, 2인방 규칙에 따른 미니맵의 탐색 영역·아군·적 표시 |
| [원소 확대](Artifacts/ingame-visuals/elements.png) | 원소 14종의 물질·형상 차이, 굴절과 입자, 접지와 국소 조명 |
| [전투](Artifacts/ingame-visuals/combat.png) | 복제한 사건 17개와 상태 표현, 입자 1,049개, 공유 조명 4개 |
| [시설 전경](Artifacts/ingame-visuals/facilities.png) | 실제 제공 모델의 원환·보라 결정/고리·갈고리·피라미드·낮은 단상 |

대기 장면의 입자는 480개, 이동은 606개였고 각 장면의 공유 점광원은 4개 이하였다. 이는 해당 제어 장면의 개수이며 전체 게임의 프레임 성능을 뜻하지 않는다. 추가 상태 캡처와 실행 기록은 `.artifacts/ingame-implementation/`에 보존했다.

## 검증과 한계

- 최종 Editor 어셈블리가 로드된 상태에서 편집 모드 **170/170 통과**. 시설 반경·지면·텍스처·깊이/그림자 패스, 새 타워와 기존 타워의 발사 높이, 지면 높이 경계, 원소 입자 예산·반경, 점광원의 선택·해제와 재초기화, 기존 UI·이동 등의 회귀 검사를 포함한다.
- Facility·Battlefield·UnitGroundShadow·ElementBody·ElementMatterParticles 셰이더 5종의 최종 오류는 0개다. [검증 요약](Artifacts/ingame-visuals/validation-summary.json)에 카탈로그와 장면별 수치를 보존했다.
- 독립 코드 리뷰에서 발견한 시설 깊이 패스 누락과 타워 발사 높이 회귀를 수정했다. 지면의 Perlin 노이즈도 범위를 제한해 0m 위로 올라오는 정점을 없앴다. 실제 카메라 검토 후 타일 무늬·큰 검은 물기 얼룩·흙/바람/번개 가시성을 보정했다.
- 기존 `Bootstrap.unity`를 저장하지 않았으며, 마무리 상태는 씬 1개·수정 표시 없음·Play 꺼짐이다. 제공 원본 FBX 5개도 작업 전후 해시가 같다.
- Unity Play, 실제 인터넷 방 조작, Windows Player 빌드와 GPU 프레임 성능은 확인하지 않았다. 시간에 따른 전투 가독성, 기본 줌의 흙·바람 대비와 방벽이 겹칠 때의 실루엣은 사람의 Play 검토가 남아 있다. 생성 시안과 실시간 결과의 재질·반사·연무 표현에는 차이가 있다.

## 주요 파일

- `Runtime/Legion/FacilityModelCatalog.cs`, `ElementalVisuals.Facilities.cs`와 `Editor/FacilityArtSetup.cs`: 실제 모델 조합과 관측·건설·유령 상태 연결.
- `Runtime/Legion/ElementalVisuals.LocalLighting.cs`, `.Glow.cs`, `.BodyParticles.cs` 및 원소 셰이더·생성 도구: 물질 표현·동작·제한된 국소 조명.
- `Runtime/Legion/BattlefieldPresentation.cs`, `Editor/BattlefieldArtSetup.cs`, `Battlefield.shader`: 실제 암석 지면·빛·물기와 자원 정리.
- `Editor/IngameVisualArtSetup.cs`, `LegionClient.prefab`: 재현 가능한 자산 구성과 실행 프리팹 연결.

위 소스 경로는 `client/Assets/_Project/Scripts/` 아래이며, 셰이더는 `client/Assets/_Project/Shaders/`, 프리팹은 `client/Assets/_Project/Prefabs/` 아래다. 재사용 자료는 [출처 기록](../third-party.md#제공-시설과-인게임-목표-시안-적용-2026-11-10)에 정리했다.
