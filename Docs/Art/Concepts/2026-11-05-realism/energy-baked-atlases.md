# 에너지 원소 베이크 아틀라스

2026-11-05에 승인된 `energy-idle-v3.png`, `energy-mobile-v2.png`를 각각 참조해 내장 image_gen 이미지 편집 도구로 생성했다. 투명 배경 옵션을 사용했고, 생성 PNG의 픽셀과 알파는 수정하지 않았다. 외부 에셋을 도입하지 않았다.

## 결과

| 상태 | 파일 | SHA-256 |
| --- | --- | --- |
| 대기 | `client/Assets/_Project/Art/BakedElements/energy-idle-atlas.png` | `B69323C2A1F4DCC996E14AE7D155B0BD4CABAB66FC23248834690FF5317D07DE` |
| 이동 | `client/Assets/_Project/Art/BakedElements/energy-moving-atlas.png` | `C67AD678AFCE1EECC8288308241DBF94EBB1A2865C04E292F7936975BE44BABD` |

두 파일은 1774×887 RGBA다. 위 행은 빛·결정·붕괴·암흑물질, 아래 행은 중력·자기장·생명·빈 칸 순서다. 4×2 UV 경계가 정수 픽셀 위치와 정확히 일치하는 해상도는 아니므로 필터링 결과를 실제 Unity 합성에서 확인해야 한다.

생성 결과를 눈으로 확인했을 때 7종의 주피사체는 해당 칸 안에 있으며 이동 꼬리는 왼쪽을 향한다. 정수 알파 8 이상을 기준으로 검사하면 모든 피사체는 각 칸 가장자리에서 떨어져 있고 빈 칸의 해당 픽셀은 없다. 원본의 낮은 알파 잔여 때문에 완전히 투명한 여백이나 빈 칸은 아니다. 빈 칸의 최대 알파는 대기 1, 이동 2다. 검은 사각 배경은 보이지 않지만 일부 피사체 주변에 회색 연무가 남아 있다. 셰이더 합성, 알파 컷오프에 따른 손실, Unity 카메라에서의 품질은 이 검토에 포함하지 않았다.

## 최종 생성 프롬프트

공통 프롬프트:

> Edit the reference concept sheet into an actual transparent-background game VFX sprite atlas, no title or scenery. Exact 4 columns by 2 equal rows, 2:1 landscape. Seven separate SMALL objects, each confined to CENTRAL 60% WIDTH AND 60% HEIGHT of its own equal grid cell, leaving a completely transparent 20% margin on ALL four sides of every cell. The ENTIRE eighth cell is transparent. No halos, stray pixels, light, smoke or dust outside the central 60% area. Never touch or cross cell boundaries. In order top row: prismatic light filaments; refractive natural crystal cluster; bright yellow-cracked eroded mineral decay; black opaque center dark matter with delicate lensing. Bottom row: white air vortex and floating rock gravity; iron filing and blue plasma magnetic field; roots moss leaves flowers life; EMPTY. Keep the reference photoreal material, detail and RTS elevated camera angle. Transparent RGBA background, not gray, black, white, checkerboard or opaque glow. No floor, shadow, text, lines, borders, rings, faces, limbs, plastic, toy or solid hoops. Each entire object visible.

대기 추가: `IDLE state: compact floating forms.` 이동 추가: `MOVING state: cores toward right with short fully contained trails flowing left.`

첫 시도에서는 광무가 칸 경계에 닿아 위 프롬프트로 한 차례 재생성했다. 실제 생성 결과에는 요청한 완전 투명 여백이 보장되지 않아 위 검사 결과를 기준으로 판단한다.
