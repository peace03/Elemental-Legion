# 대기실 소유 색상

사용자 확인: 대기실에서 자신의 색을 선택한다. 사람끼리 중복 불가, AI는 남은 색 자동 배정. 색은 건물 및 유닛·건물 바닥 링에 쓰며 원소 본체의 자연색은 유지한다. 준비 완료·연결 복구 중에는 선택을 잠근다. AI가 쓰던 색은 사람이 고르면 AI를 빈 색으로 옮긴다.

- 공유 팔레트: Colorcet glasbey_light 앞 15색을 바탕으로 #192B42 네이비 바탕 대비 3.2:1 이상으로 어두운 색만 보정한다. 흰색과 중립 회색은 플레이어 선택에서 제외한다. 번호와 이름·기존 적/중립 마커를 함께 표시한다. 모든 색각 조건에서 15색 구별을 보장한다고 주장하지 않는다.
- 서버: 색 ID를 검증하고 로비·월드·재접속·저장·호스트 승계에 보존한다. 공개 플레이어 현황 및 전송된 개체에 필요한 소유 색 메타데이터만 보낸다. 기존 wire와 바뀌므로 Protocol 버전을 올린다.
- 클라이언트: 대기실 15개 색 선택, 명단 색 표시, 서버 확인 뒤 선택 반영. 링과 건물은 소유색, 미니맵도 같은 색 사용. 원소 모델·VFX는 이번 색 작업에서 재구현하지 않는다.
- 검증: 색 중복/AI 재배정/잘못된 입력/준비·권한/저장·승계 회귀 .NET 테스트. Unity 컴파일·EditMode·대기실/전장 오프스크린 캡처. Play 및 Windows Player는 사람이 확인한다.

## 재사용 조사 (2026-11-05)

[Colorcet categorical](https://colorcet.holoviz.org/user_guide/Categorical.html), [Glasbey 색각 설명](https://glasbey.readthedocs.io/en/latest/color_vision_deficiency.html), [원본 CSV](https://github.com/holoviz/colorcet/blob/076eb84bf86c222cdac830073990f1a08c99778b/assets/Glasbey/glasbey_bw_minc_20_minl_30_n256.csv)를 참고한다. Colorcet의 CC BY 4.0 고지를 Resources/ColorcetNotice.txt에 보존하고 색 선택 화면에 출처를 표시한다. 별도 런타임 라이브러리는 도입하지 않는다. 15명 네트워크/저장 구조는 프로젝트의 기존 코드를 확장한다.
