# 자연 원소 baked 아틀라스

2026-11-05. 내장 imagegen으로 사용자 승인 시안을 편집해 4×2 아틀라스 두 장을 생성했다. 픽셀을 사후 편집하지 않았다. 게임 합성과 움직임·성능은 별도 검증이 필요하다.

- 대기 참조: `natural-idle-v3.png`; 결과 `client/Assets/_Project/Art/BakedElements/natural-idle-atlas.png`, SHA-256 `58eebb2544250eeccd417436e7ccc27527bf7cc1297441dd40a9eef2fa575dc2`.
- 이동 참조: `natural-mobile-v2.png`; 결과 `client/Assets/_Project/Art/BakedElements/natural-moving-atlas.png`, SHA-256 `7106e51b72f39fdfa512873f58e1c71c7dd3b4b159b0789d6f381f1c9cf79743`.
- 둘 다 1774×887 RGBA. 왼쪽 위부터 물·불·바람·연기 / 얼음·흙·번개·빈칸 순서. 4×2 셀 경계에 알파 16 이상 픽셀은 없지만 미세한 알파 1~15 잡음이 남는다. 빈칸의 최대 알파는 대기 1, 이동 2다. 이동 1·2·7번의 알파 16 이상 경계 여백은 약 7~8%다. 대기 1·2·3·5·7번에는 회색 경계가 보인다. 실제 셰이더의 알파 컷오프와 합성 화면에서 확인해야 한다.

## 최초 생성 공통 프롬프트

> Game-ready transparent 4-column by 2-row atlas, exactly eight equal rectangular cells, landscape 2:1. The image is a texture atlas, not a design board: perfectly uniform grid, no visible borders, no text, no background, actual transparent alpha everywhere outside subjects. Place ONE isolated subject centered in each cell, with at least 10% empty transparent padding on all four sides of each cell. Cell order strictly row-major: top row water, fire, wind, smoke; bottom row ice, earth, lightning, EMPTY transparent cell. No overlap across cell boundaries. The eighth bottom-right cell must be completely transparent. Preserve approved reference’s photorealistic material and approximate 50-degree elevated RTS view. Extract the subject identities shown in the provided reference; do not create a new art direction. No ground, shadows, circles, UI, labels, eyes, faces, limbs, toys, plastic, low-poly or solid geometric rings. Water detailed clear fluid and droplets, fire realistic orange flame volume, wind translucent vortex and beige dust, smoke dense charcoal cloud distinct from wind, ice translucent cracked shards, earth textured real mineral clump, lightning branching white electric discharge. No checkerboard or black background; genuine alpha transparency.

대기는 compact floating idle formations with organic asymmetry, 이동은 screen RIGHT, trail LEFT within cell을 추가했다.

## 채택된 2차 편집 프롬프트

대기:

> Edit this texture atlas to preserve exactly the seven illustrated subjects and their exact 4 columns by 2 rows positions; do not redesign them. Scale EACH individual subject down so all of its pixels stay inside the central 70% of its equal cell; each cell must have wide transparent margins on every side and cannot overlap neighboring cells. Delete every colored halo, haze, background residue, border and colored noise OUTSIDE each subject; transparent alpha must be zero between subjects. Keep natural individual subject details. The bottom right eighth cell must be entirely transparent. Image is a 2:1 landscape texture atlas, not a poster. No text or grid lines. Preserve fire, smoke, lightning translucent edges, but keep their entire shape within their own cell.

이동:

> Edit this texture atlas: preserve exactly seven water, fire, wind, smoke, ice, earth, lightning moving subjects in the existing 4-column by 2-row order and their rightward movement with trails to left. Remove every part of the white and gray background, grid lines, haze and board; use genuine fully transparent alpha outside each individual subject, no white rectangle or black background. Scale EACH individual subject down and center in its own cell so entire silhouettes and trails fit within central 70% of each of eight equal cells, leaving empty transparent separation and no overlap. The eighth bottom-right cell must be fully transparent. 2:1 landscape texture atlas, no borders, no letters, no floor or shadows. Preserve photographic detail of materials.
