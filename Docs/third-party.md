# 외부 자료와 고지

## LiteNetLib

- 원본: https://github.com/RevenantX/LiteNetLib / https://www.nuget.org/packages/LiteNetLib/1.3.5
- 고정 버전: **1.3.5**, MIT. 기존 의존성을 M1에서도 재사용했다.
- 고지: `shared/Plugins/LiteNetLib.LICENSE.txt`, 배포 DLL 설명은 `shared/Plugins/README.md`.
- 사용: 공유 Net 패킷·ClientConnection, .NET UDP 호스트. 원본 수정 없음.
- 검증: 동일 소스로 서버와 클라이언트 루프백 UDP 통합 테스트, .NET 빌드와 Unity 컴파일.

## Noto Sans KR

- 원본: https://github.com/notofonts/noto-cjk/tree/Sans2.004/Sans/SubsetOTF/KR
- 고정 태그: **Sans2.004**, `NotoSansKR-Regular.otf` 원본 파일.
- 원본 SHA-256: `69975a0ac8472717870aefeab0a4d52739308d90856b9955313b2ad5e0148d68` (4,644,748B).
- 라이선스: SIL Open Font License 1.1. 고지는 `client/Assets/ThirdParty/NotoSansKR/OFL.txt`에 포함한다. 원본 폰트의 저작권 정보는 폰트 메타데이터에도 유지한다.
- 사용: `client/Assets/ThirdParty/NotoSansKR/`의 수정하지 않은 원본과 `_Project/Settings/UI/LegionUiSDF.asset` 정적 TMP 아틀라스. 한글 원소명·접속 화면 글리프를 구성한다.
- 새 UPM/NuGet 패키지는 추가하지 않았다. 기존 TMP·uGUI와 Unity TextCore에서 사용한다.
- 검증: Unity 가져오기·아틀라스 생성과 EditMode의 데이터 표시 이름·화면 문구 검사. Player의 최종 시각 동작은 사람의 Play 검사 대상이다.

## Unity 제공 리소스

- 기존 `com.unity.inputsystem` 1.20.0, `com.unity.ugui` 2.6.0, URP 17.6.0을 사용한다. 각 패키지의 LICENSE/Notices와 잠금 파일을 유지한다.
- 사용자가 설치된 uGUI의 **TMP Essential Resources**를 가져왔다. `client/Assets/TextMesh Pro/`의 원본 고지·패키지 구조를 유지하며 직접 수정하지 않는다. Liberation Sans 라이선스도 해당 폴더에 보존한다.
- UI 기본 컨트롤과 URP 파티클·Unlit 셰이더는 설치된 Unity API·자산을 재사용한다. 외부 코드 복사나 대형 VFX 팩 도입은 하지 않았다.

## M2 볼륨 연무 조사와 재사용

- MIT [URP-Fog-Volumes](https://github.com/sinnwrig/URP-Fog-Volumes)의 [package.json](https://raw.githubusercontent.com/sinnwrig/URP-Fog-Volumes/main/package.json)은 URP10.x를 선언한다. URP17.6 RenderGraph와 직접 호환되지 않아 통째 도입하거나 코드를 복사하지 않았다.
- 무료 [Fog of War & Field of View](https://assetstore.unity.com/packages/tools/particles-effects/fog-of-war-field-of-view-269976)도 조사했으나 원본2022.3/현재 URP17 호환성을 검증하지 못해 도입하지 않았다.
- 설치된 URP17.6 `FullScreenPassRendererFeature`의 공개 RenderGraph/프로퍼티 블록/DrawProcedural 방식을 재사용한다. 외부 패키지 추가 없음. 직접 작성한3D 밀도장·반복 노이즈 자산은 `_Project` 안에 둔다.
- 검증: D3D11 셰이더 컴파일, 실제 RenderRequest의 세 상태, 기존 SSAO 보존과 카메라 자원 분리·해제. 측정 조건과 한계는 M2 보고서에 기록한다.

확인일: 2026-11-02. 조사 후보·미채택 이유는 M1/M2 계획과 이 문서에 기록한다.

## M3 원소 표현·효과음 (2026-11-03)

- [Unity 공식 Particle Pack Starter Assets](https://assetstore.unity.com/packages/vfx/particles/particle-pack-starter-assets-127325) **3.3**, Standard Unity Asset Store EULA. 사용자 로컬 다운로드의 SHA-256은 `a023e358cf8cfc53de10be7b8880c611e5e15a571fb6f18201cbef6c3ba52300`이다. 원본 package 전체를 임포트하지 않았다. 원본 GUID·파일을 보존한 텍스처만 `Assets/UnityTechnologies/ParticlePack/EffectExamples/` 고유 경로에 추렸다.
- 실제 표현: `Fire & Explosion Effects/Textures/LargeFlame02.tif`의8×8 애니메이션, `SmokePuff01.tif`의8×8 연무, `Legacy Particles/Textures/WaterDropParticle.tif`, `Water Effects/Textures/WaterNormal.tif`. 공식 WildFire/Steam의 타일 구성을 확인해 재사용했고 URP17.6용 머티리얼·배치 표현은 `_Project`에서 작성했다. 원본 prefabs/scripts/materials를 무차별 임포트하지 않았다.
- 호환 검토용으로 `FireExplosion.tif`, `MediumFlame03.tif`, `WaterRippleEdgeParticle.tif`, Fire/Smoke 각 `SmokePuffNormal.tif`도 추출했다. 각 파일·GUID·해시는 [M3 asset lock](Reports/M3/assets.lock.json)에 기록한다. 전체 Pack3.3의 모든 효과를 검증했다고 주장하지 않는다. 사용한 부분은 Unity6000.6/URP17.6/D3D11 컴파일·갤러리 RenderRequest 대상으로 검증한다.
- [Kenney Particle Pack1.0](https://kenney.nl/assets/particle-pack), **CC0**. 압축 원본 SHA-256 `b631d4b07f7002549fdcf155f01141ad482f79f3440e4e301eed49ce5f1d8958`. `circle_01/fire_01/magic_01/scorch_01/smoke_01/spark_01/star_01/trace_01.png`를 원본 `PNG (Transparent)` 경로에서 사용한다. 고지는 `Assets/ThirdParty/Kenney/ParticlePack/License.txt`다.
- [Kenney Impact Sounds1.0](https://kenney.nl/assets/impact-sounds), **CC0**. 압축 원본 SHA-256 `029d734af1582474edf3a694d1b0cebc97c1c152f2f39fa34d4c2bafc5de77f8`. `impactMetal_light_000/impactSoft_medium_000/impactBell_heavy_000/impactMining_000/impactGlass_light_000/impactWood_light_000/impactGeneric_light_000.ogg`를 각각 공격/피격/전향/사망/양성/UI/회복에 연결했다. 원본을 수정하지 않았으며 음높이·볼륨은 표현 설정이다. 고지는 `Assets/ThirdParty/Kenney/ImpactSounds/License.txt`다.
- 몸체 메시·셰이더·풀·노이즈·소유자 링은 프로젝트 제작 자산이다. [공식 VFX Graph 샘플](https://github.com/Unity-Technologies/VisualEffectGraph-Samples/blob/master/Packages/manifest.json)은 HDRP 의존성 때문에 통째 도입하지 않았다. 새 UPM/NuGet·물리 유체·VFX Graph 패키지를 추가하지 않았다.
- 검증 보고는 [M3](Reports/2026-11-03-m3-validation.md). 최종 아트·효과음의 사람 수용과 Windows Player 전체 프레임 확인은 별도 보류다.
