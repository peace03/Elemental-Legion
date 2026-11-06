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
- 최초 M3에서는 `LargeFlame02.tif`와 `SmokePuff01.tif`를 8×8로 분할했다. **2026-11-05 정정:** 원본 시트의 실제 프레임을 다시 확인하니 현재 사용하는 `MediumFlame03.tif`는 12×6, `SmokePuff01.tif`는 5×5다. 사실주의 작업에서 두 시트의 분할을 각각 맞췄다. `Legacy Particles/Textures/WaterDropParticle.tif`, `Water Effects/Textures/WaterNormal.tif`도 재사용하며 URP17.6용 머티리얼·배치 표현은 `_Project`에서 작성한다. 원본 prefabs/scripts/materials를 무차별 임포트하지 않았다.
- 호환 검토용으로 `FireExplosion.tif`, `MediumFlame03.tif`, `WaterRippleEdgeParticle.tif`, Fire/Smoke 각 `SmokePuffNormal.tif`도 추출했다. 각 파일·GUID·해시는 [M3 asset lock](Reports/M3/assets.lock.json)에 기록한다. 전체 Pack3.3의 모든 효과를 검증했다고 주장하지 않는다. 사용한 부분은 Unity6000.6/URP17.6/D3D11 컴파일·갤러리 RenderRequest 대상으로 검증한다.
- [Kenney Particle Pack1.0](https://kenney.nl/assets/particle-pack), **CC0**. 압축 원본 SHA-256 `b631d4b07f7002549fdcf155f01141ad482f79f3440e4e301eed49ce5f1d8958`. `circle_01/fire_01/magic_01/scorch_01/smoke_01/spark_01/star_01/trace_01.png`를 원본 `PNG (Transparent)` 경로에서 사용한다. 고지는 `Assets/ThirdParty/Kenney/ParticlePack/License.txt`다.
- [Kenney Impact Sounds1.0](https://kenney.nl/assets/impact-sounds), **CC0**. 압축 원본 SHA-256 `029d734af1582474edf3a694d1b0cebc97c1c152f2f39fa34d4c2bafc5de77f8`. `impactMetal_light_000/impactSoft_medium_000/impactBell_heavy_000/impactMining_000/impactGlass_light_000/impactWood_light_000/impactGeneric_light_000.ogg`를 각각 공격/피격/전향/사망/양성/UI/회복에 연결했다. 원본을 수정하지 않았으며 음높이·볼륨은 표현 설정이다. 고지는 `Assets/ThirdParty/Kenney/ImpactSounds/License.txt`다.
- 몸체 메시·셰이더·풀·노이즈·소유자 링은 프로젝트 제작 자산이다. [공식 VFX Graph 샘플](https://github.com/Unity-Technologies/VisualEffectGraph-Samples/blob/master/Packages/manifest.json)은 HDRP 의존성 때문에 통째 도입하지 않았다. 새 UPM/NuGet·물리 유체·VFX Graph 패키지를 추가하지 않았다.
- 검증 보고는 [M3](Reports/2026-11-03-m3-validation.md). 최종 아트·효과음의 사람 수용과 Windows Player 전체 프레임 확인은 별도 보류다.

## PixelFX (2026-11-03)

- `client/Tools/PixelFX/`에 Node.js용 이펙트 생성 도구 **0.3.0**을 설치했다. GitHub 커밋 `a84159688af3ca37a4be05a0d422b94451b2e24e`와 배포본 무결성 해시는 npm 잠금 파일에 고정했다. [원본 저장소](https://github.com/patch-gg/pixelfx/tree/a84159688af3ca37a4be05a0d422b94451b2e24e)
- 생성 도구와 효과 코드는 MIT다. 설치본의 `LICENSE`와 `NOTICE.md`에 Patch, Hugo Duprez, Hollow Crown 출처가 보존된다. 갤러리의 Patch Pixel 폰트에는 별도 저작권 고지가 적용된다. [라이선스](https://github.com/patch-gg/pixelfx/blob/a84159688af3ca37a4be05a0d422b94451b2e24e/LICENSE), [고지](https://github.com/patch-gg/pixelfx/blob/a84159688af3ca37a4be05a0d422b94451b2e24e/NOTICE.md)
- Node.js 18 이상에서 저장소 루트를 기준으로 아래 명령을 실행한다. 첫 줄은 새 체크아웃에서 설치할 때 사용한다. Unity에서는 생성된 PNG를 스프라이트로 가져오고 JSON에서 프레임 위치와 재생 시간을 확인한다.

```powershell
npm.cmd ci --prefix client/Tools/PixelFX --ignore-scripts
npm.cmd --prefix client/Tools/PixelFX run render -- ice/frost-nova --sheet --no-actor --bg none --out ../../../.artifacts/pixelfx
```

- 잠금 파일로 설치를 재현했고, `ice/frost-nova`의 원본 검사가 통과했다. 103프레임 투명 PNG와 JSON의 프레임 영역·재생 시간을 확인했다. Unity 임포트·씬 연결·Play 검증은 별도다.

## Graphify (2026-11-03)

- 설치된 `graphifyy` **0.9.63**을 코드 관계 탐색에 사용한다. 설치본의 라이선스 식별자는 Apache-2.0이며 `LICENSE`, `LICENSE-MIT`, `NOTICE`를 포함한다. [고정 버전 배포본](https://pypi.org/project/graphifyy/0.9.63/)
- 프로젝트의 [분석 범위와 갱신 절차](Rules/graphify.md)를 적용해 로컬 AST 색인을 만들었다. Unity·서버·공유 코드가 포함되며, `UdpGameServer → NetworkWorld → SimulationWorld` 조회 경로를 확인했다.

## .NET 시드 호환 난수 (2026-11-05)

- [dotnet/runtime v10.0.0의 Random.CompatImpl.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Random.CompatImpl.cs)의 시드 기반 알고리즘을 `shared/Runtime/Sim/StatefulRandom.cs`에 맞게 옮겼다. 출처 버전은 `v10.0.0`으로 고정하며 내부 상태의 복사·복원과 입력 검증을 추가했다.
- 원본과 수정본에는 MIT 라이선스가 적용된다. 고지는 `shared/Runtime/Sim/StatefulRandom.LICENSE.txt`에 보존한다. C# 9와 netstandard2.1에서 기존 공유 난수 인터페이스를 유지하고 새 패키지를 추가하지 않았다. [원본 라이선스](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT)
- .NET 10에서 0·양수·음수·정수 최솟값·최댓값 시드를 사용해 `Random`의 범위·실수·바이트 추출을 교차 비교했다. 승계 전후에는 생태·AI·전투 진행의 상태 일치를 검증한다. Unity Editor 검증은 승계 통합 검증에서 확인한다.

## Cloudflare Quick Tunnel (2026-11-03)

- [Cloudflare cloudflared 2026.10.7](https://github.com/cloudflare/cloudflared/releases/tag/2026.10.7) Windows x64 실행 파일을 친구 테스트에 사용한다. 런처가 공식 릴리스의 SHA-256과 일치하는지 확인한 뒤 저장소의 무시된 작업 폴더에서 실행한다. 게임 빌드에는 임시 주소만 포함한다. [Apache-2.0 라이선스](https://github.com/cloudflare/cloudflared/blob/2026.10.7/LICENSE) · [보안 권고](https://github.com/cloudflare/cloudflared/security/advisories)
- 이 도구를 이용해 공개 HTTPS 주소와 WebSocket 중계를 구성한다. 공개 주소 확인과 Unity 빌드 연동은 기존 프로젝트 실행기에 연결한다. [Quick Tunnel](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/) · [WebSocket 지원](https://developers.cloudflare.com/network/websockets/)

## Colorcet 소유 색상 팔레트 (2026-11-05)

- [Colorcet](https://colorcet.holoviz.org/user_guide/Categorical.html)의 `glasbey_light`에 해당하는 [원본 CSV](https://github.com/holoviz/colorcet/blob/076eb84bf86c222cdac830073990f1a08c99778b/assets/Glasbey/glasbey_bw_minc_20_minl_30_n256.csv)를 참고했다. 커밋 `076eb84bf86c222cdac830073990f1a08c99778b`로 고정했다. HoloViz / Peter Kovesi 출처와 [CC BY 4.0](https://github.com/holoviz/colorcet/blob/076eb84bf86c222cdac830073990f1a08c99778b/LICENSE.txt) 전문은 `client/Assets/_Project/Resources/ColorcetNotice.txt`에 보존한다.
- 앞 15색을 선택하고, 어두운 색만 흰색 방향으로 보정해 네이비 `#192B42` 바탕에서 3.2:1 이상의 sRGB 대비를 확보했다. 적용 값은 `shared/Runtime/Data/PlayerPalette.cs` 한 곳에서 관리한다. 대기실에 출처·수정 사실을 표시하며 별도 런타임 라이브러리는 추가하지 않았다.
- 이 대비는 지정 UI 바탕의 평면 색 기준이다. 실제 3D 조명·바탕과 모든 색각 조건에서 15색 구별을 보장하지 않는다. 색상 번호·참가자 이름·적/중립 마커 형태를 함께 유지한다.

## Poly Haven 원소 표면 (2026-11-05)

- Rob Tuytel의 [Rock 08](https://polyhaven.com/a/rock_08)과 [Mossy Rock](https://polyhaven.com/a/mossy_rock) 1K JPEG 색상·OpenGL 노멀·거칠기 맵 총 6개를 `client/Assets/_Project/Art/Surfaces/`에 보존한다. [공식 CC0 조건](https://polyhaven.com/license)에 따라 사용하며 전문은 같은 폴더의 `CC0-1.0-legalcode.txt`에 있다.
- 원본 파일은 수정하지 않았다. 공식 API의 크기·MD5, 1024×1024 이미지 디코딩을 확인했다. 파일별 다운로드 URL과 SHA-256은 [표면 자료 기록](Art/Concepts/2026-11-05-realism/material-sources.md)에 고정했다. 셰이더에서 색상·노멀·거칠기 맵을 각각 읽어 알파·부드러움 채널을 혼동하지 않는다.
- 프로젝트 생성 도구에서 노멀을 NormalMap, 거칠기를 선형 데이터, 색상을 sRGB로 가져온다. 별도 런타임 패키지는 추가하지 않는다. 실제 적용과 검증은 원소 사실주의 작업 보고에 기록한다.
- 같은 CC0 자료인 [Studio Small 08](https://polyhaven.com/a/studio_small_08)의 1K Radiance HDR도 반사 환경으로 사용한다. 원본 1,508,872바이트의 크기·MD5와 SHA-256은 표면 자료 기록에 보존한다. Unity에서는 256면 RGBAHalf·선형·Specular mip Cubemap으로 가져와 원소 머티리얼에만 연결한다. 씬의 전역 하늘·조명 설정은 변경하지 않는다.

## 원소 사진 본체 실험 보관 (2026-11-05~06)

- 사용자가 승인한 대기·이동 시안을 내장 imagegen으로 편집해 `client/Assets/_Project/Art/BakedElements/`의 자연·에너지 대기·이동 아틀라스 네 장을 제작했다. 1774×887 RGBA, 4×2 배치이며 마지막 칸은 사용하지 않는다. 생성 PNG를 사후 픽셀 편집하지 않았다.
- 외부 사진·에셋의 파일을 이 시트에 복사하지 않았다. 참조 시안, 프롬프트, SHA-256과 알파 여백의 한계는 [자연 원소 제작 기록](Art/Concepts/2026-11-05-realism/baked-atlases.md), [에너지 원소 제작 기록](Art/Concepts/2026-11-05-realism/energy-baked-atlases.md)에 남겼다. AI 시안과 실제 Unity 화면의 검증 결과는 구분한다.
- 2026-11-06에 사진 본체 이미지와 연결을 프로젝트에서 제거하고 `.artifacts/live-elements/before/`로 옮겼다. 현재 원소는 실시간 3D 재질과 Particle System을 사용한다. 설치된 Particle Pack의 플립북은 개별 연기·불꽃 입자에 쓰인다. [실시간 원소 그래픽](Reports/2026-11-06-live-elements.md)
