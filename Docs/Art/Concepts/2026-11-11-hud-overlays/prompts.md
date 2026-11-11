# 현황·메뉴·설정·소개창 시안의 생성 기록

2026-11-11에 내장 `image_gen`으로 생성했다. 입력 이미지는 [앞서 만든 기본 HUD](../2026-11-11-hud-panels/hud-base.png)다. 전장과 HUD를 같은 상태로 두고 창을 하나씩 열어 배치를 검토했다.

| 화면 | 최종 이미지 |
| --- | --- |
| 현황판 | [hud-dashboard.png](hud-dashboard.png) |
| ESC 메뉴 | [hud-escape.png](hud-escape.png) |
| 설정창 | [hud-settings.png](hud-settings.png) |
| 건물 소개 | [hud-building-info.png](hud-building-info.png) |
| 원소 소개 | [hud-element-info.png](hud-element-info.png) |

현황판은 내 군단과 공개 점수를 표시한다. ESC 메뉴는 온라인 전투가 계속됨을 안내한다. 설정은 음향 탭을 연 상태다. 건물과 원소의 소개는 포집기와 물을 예로 들었다. 플레이어의 현황·순위·음량은 예시이며, 건물과 원소의 수치는 생성 당시 `shared/Data/gamedata.json`에서 확인했다.

화면의 명칭과 표시 범위는 `ClientScreen.cs`, `ClientSettingsScreen.cs`, `BuildingTooltip.cs`, UI·UX 기획서와 메인 게임 개선안을 참고했다. 실제 Play에서의 가독성·입력·저장은 검증하지 않았다. 생성 이미지의 문구와 배치를 눈으로 확인했으며 복사본은 SHA256 비교로 확인했다.

아래 프롬프트를 화면마다 독립적으로 사용했다. 입력 이미지는 공통이며 `transparent_background`는 `false`다.

## 현황판

```text
Use case: precise-object-edit, shippable-quality Korean PC game UI concept. Image 1 is the exact edit target. It shows Elemental Legion, an oblique overhead 3D elemental army on dark slate ground with bronze-and-cyan cosmic buildings. Preserve this exact 16:9 landscape gameplay frame, camera, units, buildings, textures, and the compact edge HUD. Keep the upper-left “핵 48” / “인구 40/100”, upper-right “본진” HP bar “2,000 / 2,000” and B, lower-left square minimap with badges 1,2,3,4 and its three cyan dots, bottom-center squad key tiles 1–5 with their current counts and “R 양자 이동”, and lower-right “양성”, “병력”, “건설”. The temporary cyan “4 → 2 · 3기 출격” feedback has expired; remove that chip only.

The added interface must belong to this same design: matte near-black surfaces, very fine subdued brass perimeter, clean Korean sans-serif, near-white text, cyan active selection, restrained glow. The priority is readable practical UI at actual game scale and generous spacing. Render every quoted Korean label accurately. Show only the specified interface state. No external labels, layout board, illustration title, logo, watermark, developer notes, or extra controls. One complete full-bleed landscape frame.

Add one centered wide “현황” panel, about 65% of frame width and 53% of height. Keep the minimap and lower control bar entirely visible. The panel is dark enough for perfect text readability while the battlefield outside stays undimmed and perceptible.
Header “현황” at left; small “Tab 키를 떼면 닫기” at upper right.
Under the heading, compact tabs “내 군단” (active) and “여정 기록”; a small separate “상성표” entry toward the right.
Use two balanced content columns. Left column is about 55%, a short personal summary plus squad list. Show the three totals “핵 48”, “인구 40/100”, “본진 4단계”, then the quiet status line “완공 시설 4 · 증원 3기 · 예약 0”. Under that show four clear rows “1  28기  조종 중”, “2  5기  증원 3기 이동 중”, “3  3기  대기”, “4  수비 1기  본진”. Row 1 has the subtle cyan selection edge.
Right column title “세력 현황”; a straightforward table with columns “순위”, “닉네임”, “점수”. Four example rows: “1  성운  268”, “2  플레이어  220”, “3  서리  184”, “4  잔광  156”. Highlight only the row with 플레이어 as the owner. Display only these public aggregate scores for other players. No enemy army counts, hidden facilities, resources, position information, match countdown or kill feed. Keep the panel refined and compact; use fine separators and few borders.
```

## ESC 메뉴

```text
Use case: precise-object-edit, shippable-quality Korean PC game UI concept. Image 1 is the exact edit target. It shows Elemental Legion, an oblique overhead 3D elemental army on dark slate ground with bronze-and-cyan cosmic buildings. Preserve this exact 16:9 landscape gameplay frame, camera, units, buildings, textures, and the compact edge HUD. Keep the upper-left “핵 48” / “인구 40/100”, upper-right “본진” HP bar “2,000 / 2,000” and B, lower-left square minimap with badges 1,2,3,4 and its three cyan dots, bottom-center squad key tiles 1–5 with their current counts and “R 양자 이동”, and lower-right “양성”, “병력”, “건설”. The temporary cyan “4 → 2 · 3기 출격” feedback has expired; remove that chip only.

The added interface must belong to this same design: matte near-black surfaces, very fine subdued brass perimeter, clean Korean sans-serif, near-white text, cyan active selection, restrained glow. The priority is readable practical UI at actual game scale and generous spacing. Render every quoted Korean label accurately. Show only the specified interface state. No external labels, layout board, illustration title, logo, watermark, developer notes, or extra controls. One complete full-bleed landscape frame.

Show the in-game ESC menu as one small centered vertical panel, about 25% of frame width and 36% of height. Slightly darken the surrounding world, with no blur, so the live battlefield and all edge HUD remain recognizable.
Inside the panel, title “게임 메뉴”; a small “Esc” keycap at the upper right; one short quiet but clear line “온라인 전투는 계속됩니다.”
Below it exactly three wide equally sized buttons in a vertical stack: “계속하기” (cyan primary), “설정”, “종료하기”. Give the buttons comfortable click targets and spacing. No second dialog, branding banner, save/load options, or other text. The layout should feel immediately familiar and efficient.
```

## 설정창

```text
Use case: precise-object-edit, shippable-quality Korean PC game UI concept. Image 1 is the exact edit target. It shows Elemental Legion, an oblique overhead 3D elemental army on dark slate ground with bronze-and-cyan cosmic buildings. Preserve this exact 16:9 landscape gameplay frame, camera, units, buildings, textures, and the compact edge HUD. Keep the upper-left “핵 48” / “인구 40/100”, upper-right “본진” HP bar “2,000 / 2,000” and B, lower-left square minimap with badges 1,2,3,4 and its three cyan dots, bottom-center squad key tiles 1–5 with their current counts and “R 양자 이동”, and lower-right “양성”, “병력”, “건설”. The temporary cyan “4 → 2 · 3기 출격” feedback has expired; remove that chip only.

The added interface must belong to this same design: matte near-black surfaces, very fine subdued brass perimeter, clean Korean sans-serif, near-white text, cyan active selection, restrained glow. The priority is readable practical UI at actual game scale and generous spacing. Render every quoted Korean label accurately. Show only the specified interface state. No external labels, layout board, illustration title, logo, watermark, developer notes, or extra controls. One complete full-bleed landscape frame.

Show one centered medium-width settings window occupying about 60% of the frame width and 55% of its height, with the battlefield gently darkened behind it. Header “설정”, close × at top right.
Left sidebar contains exactly four generously spaced text tabs: “음향” selected cyan, “화면”, “표현”, “조작 키”. A fine vertical separator divides the sidebar from the content.
Right content shows the selected audio page only. Three clean horizontal slider rows. First row label “전체 음량” and numeric value “80%”, second “효과음” and “70%”, third “UI” and “60%”. Each row has a well-spaced slim cyan slider track at the appropriate percentage, a small unselected checkbox labeled “음소거” and a small speaker icon button for audio preview. Keep the three slider controls uniform and precisely aligned. Show no BGM/music slider.
At bottom left small text “변경 즉시 저장”, and at bottom right one small button “돌아가기”. Sliders have visible handles and their values are clear. Do not add an apply button on this audio page or any unrelated settings.
```

## 건물 소개

```text
Use case: precise-object-edit, shippable-quality Korean PC game UI concept. Image 1 is the exact edit target. It shows Elemental Legion, an oblique overhead 3D elemental army on dark slate ground with bronze-and-cyan cosmic buildings. Preserve this exact 16:9 landscape gameplay frame, camera, units, buildings, textures, and the compact edge HUD. Keep the upper-left “핵 48” / “인구 40/100”, upper-right “본진” HP bar “2,000 / 2,000” and B, lower-left square minimap with badges 1,2,3,4 and its three cyan dots, bottom-center squad key tiles 1–5 with their current counts and “R 양자 이동”, and lower-right “양성”, “병력”, “건설”. The temporary cyan “4 → 2 · 3기 출격” feedback has expired; remove that chip only.

The added interface must belong to this same design: matte near-black surfaces, very fine subdued brass perimeter, clean Korean sans-serif, near-white text, cyan active selection, restrained glow. The priority is readable practical UI at actual game scale and generous spacing. Render every quoted Korean label accurately. Show only the specified interface state. No external labels, layout board, illustration title, logo, watermark, developer notes, or extra controls. One complete full-bleed landscape frame.

Show one compact contextual building information card right-aligned above the existing bottom-right buttons, about 27–28% of frame width and 41% of height. It is an introduction/inspection card for the friendly “포집기”. Keep the central army and battlefield completely uncovered and undimmed.
Card header “포집기”, quiet subtitle “자원 수집 시설”, small close ×. A tasteful small 3D thumbnail shows a low bronze cosmic collecting device with nested circular rings and a blue-violet core, matching the small round facility near the lower-left in Image 1. The thumbnail belongs inside the card; no new building on the field.
Below the thumbnail clear two-line description “주변에 떨어진 핵을 자동으로 수집합니다.” Then two airy stat rows: “체력” with “400 / 400”, “수집 반경” with “40m”. Bottom line “건설 20핵 · 5초”. A small “가동 중” status in cyan can be beside the title.
Subtly highlight the existing small round bronze-and-violet facility at x=21%, y=62% of the battlefield with a thin cyan selection ring. Do not change its architecture, position, or size and do not show a build hologram. All text must fit with clear padding, no ellipsis or additional action buttons.
```

## 원소 소개

```text
Use case: precise-object-edit, shippable-quality Korean PC game UI concept. Image 1 is the exact edit target. It shows Elemental Legion, an oblique overhead 3D elemental army on dark slate ground with bronze-and-cyan cosmic buildings. Preserve this exact 16:9 landscape gameplay frame, camera, units, buildings, textures, and the compact edge HUD. Keep the upper-left “핵 48” / “인구 40/100”, upper-right “본진” HP bar “2,000 / 2,000” and B, lower-left square minimap with badges 1,2,3,4 and its three cyan dots, bottom-center squad key tiles 1–5 with their current counts and “R 양자 이동”, and lower-right “양성”, “병력”, “건설”. The temporary cyan “4 → 2 · 3기 출격” feedback has expired; remove that chip only.

The added interface must belong to this same design: matte near-black surfaces, very fine subdued brass perimeter, clean Korean sans-serif, near-white text, cyan active selection, restrained glow. The priority is readable practical UI at actual game scale and generous spacing. Render every quoted Korean label accurately. Show only the specified interface state. No external labels, layout board, illustration title, logo, watermark, developer notes, or extra controls. One complete full-bleed landscape frame.

Show one contextual element information card right-aligned above the existing bottom-right buttons, about 28% of frame width and 50% of height. Inspect a friendly “물” elemental. Keep the battlefield outside undimmed and the central army visible.
Card header “물”, quiet subtitle “자연 · 원거리”, small close ×. Use a small high-fidelity thumbnail of the actual game's blue liquid elemental: a floating swirling translucent blue water mass, no humanoid character. Style like the water creature in Image 1.
Below the thumbnail an aligned compact 2×2 stat arrangement with exact labels and values: “체력 70”, “공격력 9”, “공격 속도 1.1회/초”, “사거리 6m”.
Then one effect line “젖음 4초” and the short description “공격한 적을 젖게 만듭니다.”
Then a clear “시너지” heading and exactly three small readable icon-and-text rows: water plus lightning icons with “감전”, water plus ice icons with “빙결”, water plus fire icons with “증기”. Keep the pairs and their names easy to distinguish.
Bottom quiet line “주력 양성 8핵 · 6초”. No long lore paragraphs, extra abilities, purchase action, or extra windows. Fit everything cleanly inside the compact card with no clipped or invented text.
```

