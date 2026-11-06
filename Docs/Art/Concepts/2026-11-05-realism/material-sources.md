# 원소 표면 자료 출처

확인일: 2026-11-05. Poly Haven은 자체 에셋을 CC0로 공개하며 상업적 사용·수정·재배포를 허용한다. [공식 라이선스](https://polyhaven.com/license), [CC0 전문](https://creativecommons.org/publicdomain/zero/1.0/legalcode.txt). 전문 사본: `client/Assets/_Project/Art/Surfaces/CC0-1.0-legalcode.txt`.

각 파일은 공식 API의 크기와 MD5에 일치함을 확인했으며 아래 SHA-256으로 내용을 고정한다. 원본을 수정하지 않았다. Unity 임포트와 화면 검증은 아직 하지 않았다.

## Rock 08 / Mossy Rock

- [Rock 08](https://polyhaven.com/a/rock_08), Rob Tuytel: 흙·붕괴의 거친 광물 후보.
- [Mossy Rock](https://polyhaven.com/a/mossy_rock), Rob Tuytel: 생명 중심부의 이끼 낀 표면 후보.
- 1K JPEG 원본의 색상, OpenGL 노멀, 거칠기 맵만 가져왔다. 별도 채널 재배치는 하지 않았다.

- `rock_08` / Diffuse: `client/Assets/_Project/Art/Surfaces/rock_08/rock_08_diff_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/rock_08/rock_08_diff_1k.jpg)
  - SHA-256: `653c407f1effd9df8ee9366c0f85b585770856121d8810efd11e54e8707a8031`
- `rock_08` / nor_gl: `client/Assets/_Project/Art/Surfaces/rock_08/rock_08_nor_gl_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/rock_08/rock_08_nor_gl_1k.jpg)
  - SHA-256: `b77a9bacd0ff54adfab7c04960b226f57ab187f7a0966e624f15ecc45c5f8f24`
- `rock_08` / Rough: `client/Assets/_Project/Art/Surfaces/rock_08/rock_08_rough_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/rock_08/rock_08_rough_1k.jpg)
  - SHA-256: `df9c1699ae4044cb4492ce2f2ac62be3cb3139184a4d41a7ef11efb050b3543c`
- `mossy_rock` / Diffuse: `client/Assets/_Project/Art/Surfaces/mossy_rock/mossy_rock_diff_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/mossy_rock/mossy_rock_diff_1k.jpg)
  - SHA-256: `4dbf37f1b0465c9c8914213163923be159716f5b97fb6946437b6c5e143ef540`
- `mossy_rock` / nor_gl: `client/Assets/_Project/Art/Surfaces/mossy_rock/mossy_rock_nor_gl_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/mossy_rock/mossy_rock_nor_gl_1k.jpg)
  - SHA-256: `232cdd6a9fdfd01bb8bb1f8f604c53dbca79fa0d3d7995edf85c9945757aa8df`
- `mossy_rock` / Rough: `client/Assets/_Project/Art/Surfaces/mossy_rock/mossy_rock_rough_1k.jpg`
  - [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/mossy_rock/mossy_rock_rough_1k.jpg)
  - SHA-256: `0a18bdeb08444b730170b0477f13940ce6cd4d82d06137df661f96d1e2a6a66e`

## Studio Small 08

- [Studio Small 08](https://polyhaven.com/a/studio_small_08), Sergej Majboroda. CC0. 반사만 제공할 소프트박스 HDRI 후보이며 씬 조명이나 배경에는 사용하지 않는다.
- 원본 1K Radiance HDR: `client/Assets/_Project/Art/Surfaces/studio_small_08/studio_small_08_1k.hdr`
- [다운로드 원본](https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/1k/studio_small_08_1k.hdr)
- SHA-256: `f6a989f89432eb4eee3191364a9c1ceed195c4ec3544173a3c04fd96cb91d0ba`
- 공식 API의 크기와 MD5 일치, Radiance 헤더 확인. Unity에서 256 픽셀 정육면체·RGBAHalf·Specular convolution·mipmap·linear 설정을 확인했다. 반사 효과는 갤러리 캡처에서 물 표면에 확인했으며 성능은 아직 측정하지 않았다.
