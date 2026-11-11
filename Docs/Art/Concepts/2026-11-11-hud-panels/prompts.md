# HUD 창 시안의 생성 기록

2026-11-11에 내장 `image_gen`으로 생성했다. 앞선 대화의 기본 HUD 시안을 입력 이미지로 사용했다. 게임 화면을 어떻게 배치할지 검토하는 이미지이며, 실제 Unity Play 화면과 조작 검증은 별도로 필요하다.

| 화면 | 이미지 |
| --- | --- |
| 입력으로 사용한 기본 HUD | [hud-base.png](hud-base.png) |
| 양성 창 | [hud-training.png](hud-training.png) |
| 병력 창 | [hud-forces.png](hud-forces.png) |
| 건설 창 | [hud-construction.png](hud-construction.png) |

세 창은 오른쪽 아래 같은 영역에서 하나씩 열리는 구성으로 그렸다. 양성 수량과 목적 분대는 버튼으로 선택한다. 병력 화면은 합류 인원과 이동 중인 증원을 나누어 표시한다. 건설 화면은 선택한 시설의 배치를 미리 보여 준다. 수치와 상태는 화면 배치를 보여 주는 예시다. 비용과 양성 상한은 생성 당시 `shared/Data/gamedata.json`에서 확인했다.

아래 공통 프롬프트 뒤에 각 화면의 프롬프트를 붙여 독립적으로 생성했다. 투명 배경 옵션은 `false`로 설정했다. 생성한 이미지를 눈으로 확인하고 원본과 복사본의 SHA256을 비교했다.

## 공통 프롬프트

```text
Use case: precise-object-edit, high-fidelity game UI mockup. Image 1 is the exact edit target, a 16:9 Elemental Legion gameplay frame. Reuse the identical framing, rendered battlefield, terrain, buildings, squads and existing HUD, including the upper resource and base capsules, lower-left minimap with numerals 1–4 and the three cyan dots, and bottom-center 1–5/R buttons. This is a matched comparison of the same scene with a right-side window open. The former temporary center “4 → 2 · 3기 출격” chip has now faded: remove just that chip. The three lower-right buttons “양성”, “병력”, “건설” remain in their original row and positions. Only the specified active button has the brighter cyan edge.

Add just one compact contextual panel, right-aligned directly above the existing lower-right buttons with a tiny gap, about 27–28% of the whole frame width and at most 39% of its height. It opens upward within the right-hand quarter of the screen. Leave the entire center battlefield uncovered and undimmed. Match the reference charcoal matte panels, fine muted brass outline, cyan interaction accent, Korean modern sans typography, and breathing space. Prioritize clean high-contrast 1:1 UI text, with adequate glyph size. No new logo, title outside the panel, annotation, arrows pointing from outside the panel, device frame, multiple panels, or extra overlay. Deliver one full-bleed 16:9 screenshot mockup.
```

## 양성 창 프롬프트

```text
Open “양성”. The bottom-right “양성” trigger is active. Panel contents top to bottom:
• Header “양성”, small close ×. A compact two-tab row “자연” and “에너지” with “자연” selected.
• Seven small, finely rendered elemental icon tiles in two compact rows, exactly named “흙”, “얼음”, “불”, “연기”, “물”, “바람”, “번개”. Reuse the distinctive elements from the reference game. “물” is visibly selected with a cyan outline and drop icon.
• In one compact row two separate groups of four clickable numbered chips. Under “수량”, chips “1  2  3  4”, select “3”. Under “목적 분대”, chips “1  2  3  4”, select “2”. The chips are click presets, no text entry or plus/minus stepper. Make the two groups clearly distinct.
• Footer with “기당 6초” and “예약 없음” in quiet text and one clear action “물 3기 양성 · 24핵”. This screen is just before confirming, so the original “핵 48” and “인구 40/100” remain.
Use the exact Korean labels above. Do not add further rows or instructions.
```

## 병력 창 프롬프트

```text
Open “병력”. The bottom-right “병력” trigger is active. Panel contents top to bottom:
• Header “병력”, small close ×. Tabs “분대” and “원소 구성”, with “분대” active.
• Four clean compact rows, each with one numeric badge at left, the joined count centrally and its status at right. Exactly: row “1” with “28기” and “조종 중” (cyan active outline); row “2” with “5기” and “증원 3기 이동 중” (small cyan moving-dot icon before status); row “3” with “3기” and “대기”; row “4” with “수비 1기” and “본진”. No row 5, since 5 is the existing separate whole-field selection key. Ensure the words and numbers are immediately legible and aligned, no table grid.
• One small clear bottom button “재편” on the right, the same compact type as the existing HUD. Nothing else expanded. Match the 40 living units shown at upper left (37 joined, 3 traveling); visually distinguish the three en route from the joined 5 in squad 2 without adding arithmetic or a legend.
Use the exact Korean labels above.
```

## 건설 창 프롬프트

```text
Open “건설”. The bottom-right “건설” trigger is active. Panel contents top to bottom:
• Header “건설”, small close ×.
• Five compact facility choices, in a clear grid or short list with small bronze-and-teal game facility icons. Exact names and prices: “포집기 20핵”, “응집로 30핵”, “수호 위성 40핵”, “점화로 55핵”, “자기권 75핵”. “포집기” is selected (cyan rim); “점화로” and “자기권” are dimmed as unaffordable while their text remains readable. Do not show other purchase buttons.
• Just below choices, selected detail “5초 · 주변 핵 수집”. Bottom placement hint “좌클릭 배치 · 우클릭 취소”. The original top-left resource 48 remains because placement is previewed.
• On the actual ground in a clear empty spot just below and to the right of the large blue home core on the left, about x=30% y=62%, show one small transparent cyan hologram of the collector building with a thin valid-placement footprint. A small low metallic device with a cyan core, fitted to the reference camera perspective. Add no global grid or large screen effect and do not cover an existing unit or facility.
Use the exact Korean labels above. Apart from this single hologram and the panel, keep the reference frame.
```
