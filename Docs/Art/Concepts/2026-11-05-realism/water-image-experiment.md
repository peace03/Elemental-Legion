# 물 원소 투명 이미지 실험

2026-11-05. 내장 imagegen으로 승인된 대기·이동 시안의 물만 별도 이미지로 생성했다. 승인 보드는 수정하지 않았다. 게임 화면의 실제 합성과 성능은 아직 확인하지 않았다.

- 대기 참조: `natural-idle-v3.png`의 왼쪽 위 물.
- 이동 참조: `natural-mobile-v2.png`의 왼쪽 위 물.
- `client/Assets/_Project/Art/BakedElements/water-idle.png`: 1254×1254 RGBA, SHA-256 `c66f2f081d1bc7f8d3c00b8cbc5304c2103f0e296f5eb665aeeb9905bdf05cef`.
- `client/Assets/_Project/Art/BakedElements/water-moving.png`: 1254×1254 RGBA, SHA-256 `c3d58fb60fb1fc948b8fc15156ba9046228a32ab96e8fc723806e963b89d7378`.
- `client/Assets/_Project/Art/BakedElements/water-idle-clean.png`: 대기본 경계 정리 대안, 1254×1254 RGBA, SHA-256 `53a838284c0331387a594a37514d74c8917b8de972a6475b3393fae6f16617e9`.
- 세 이미지의 네 모서리 알파는 0이고 부분 투명을 포함한다. 이미지 가장자리 주변의 청록색 잔여물은 실제 합성에서 확인해야 한다. 이동 정리 재생성은 불투명한 배경이 보여 채택하지 않았다.

## 프롬프트

대기:

> Edit target: the approved concept board. Create a game-ready isolated VFX cutout of ONLY the water elemental shown in the upper-left panel, retaining its photoreal water material and approximately 50-degree elevated RTS camera view. One compact asymmetric floating mass of clear blue water, delicate curling thin sheets, tiny detached droplets and white specular highlights. Square 1024 or larger composition, centered, about 12% clear margin. Real transparent alpha background, including between all separated droplets. Remove board, all other six elements, letters, ground, cast shadow, ownership ring and every background element. No black rectangle, no eyes, face, limbs, creature or thick geometric ring. This is a pre-rendered realistic VFX texture for a game, not a toy or plastic sphere.

이동:

> Edit target: the approved concept board. Create a game-ready isolated VFX cutout of ONLY the water elemental shown in the upper-left panel, retaining photoreal water material and approximately 50-degree elevated RTS camera view. One asymmetric cohesive mass of clear blue water, moving toward image RIGHT with a flowing wake, fine spray and trailing droplets toward image LEFT. Thin curling water sheets and bright white specular highlights; same recognizable water material as an idle version. Square 1024 or larger composition, centered, about 12% clear margin. Real transparent alpha background, including between separated droplets. Remove board, all other six elements, letters, ground, cast shadow, ownership ring and every background element. No black rectangle, no eyes, face, limbs, creature or thick geometric ring. This is a pre-rendered realistic VFX texture for a game, not a toy or plastic sphere.

대기 경계 정리:

> Clean this game VFX cutout. Keep the central water elemental shape, blue water, bright highlights, detached droplets and camera angle unchanged. Remove all gray haze, cyan/blue outlines, noisy fringe, halos and background residue around it. Transparent alpha must be perfectly clean outside the water, including between droplets; no glow or cast shadow. Place isolated subject centrally with clear transparent padding around all edges. Preserve thin transparent water sheets and natural water detail. No text, ground, ring, face or extra objects.
