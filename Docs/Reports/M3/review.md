# M3 독립 최종 리뷰

실행 계획의 마지막 fresh-context 읽기 전용 검토1회를 수행했다. 기준 HEAD는d61aba598244f6437dbd72a17dd3a2ccb2341197이며 M1/M2·사용자 미커밋 작업과 M3 범위를 구분했다. 리뷰어는 파일 변경·하위 리뷰 위임·Play·전체 suite 재실행을 하지 않았다.

결과: **Critical0 /Important1 /Minor0**.

Important: Start-Server.ps1의 기본 normal을 `--scenario normal`로 전달하지만 ServerOptions는 m3-demo만 허용해 일반 실행이 실패했다. 이전 DLL의 같은 인수로 재현했다. root에서 normal/listen필수/명시적중복 테스트를 RED로 고정한 뒤 normal/m3-demo를 허용하고 별도의 scenarioSpecified를 검사하도록 수정했다. 최종 전체 .NET148/148·경고/오류0과 런처 Release 재빌드를 확인했다.

시야 재검사/숨은 출처 제거, 신뢰 결과 보존/제한, typed Forget, 과거 전향/사망 복원 차단, 카메라 지정과 효과·음향 정리에서는 추가 중대 결함을 찾지 못했다.

판정 유보와 root의 처리:

- 최종 아트·가독성: 사람의 수용으로 남긴다. 자동 완료로 바꾸지 않는다.
- Play/WindowsPlayer 조작·두 화면·1080p60fps: 사람 확인으로 남긴다. 카메라 RenderRequest 수치로 대체하지 않는다.
- 과거 M0 밀집13.919ms: 승인된 별도 최적화 보류를 유지한다. 최신10.934ms 미달도 함께 기록한다.
- 성능 수치·최종 문서: root가 원문·실제 결과와 대조해 게시한다. 리뷰어가 측정했다고 주장하지 않는다.
- 슬롯 재사용·계정 복원·정규 중립 생성 등의 M4/M5: 승인 범위 밖이며 이번에 구현하지 않는다.
