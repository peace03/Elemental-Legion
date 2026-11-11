# 건물 우회 중 멈칫과 위치 튐

네트워크 없이 한 분대를 건물 너머로 이동시켰다. 서버는 우회 경유점에 닿으면 해당 틱의 남은 이동량을 버렸다. 클라이언트는 우클릭으로 진행 중인 서버 이동에도 직접 입력용 위치 보정을 누적했다. 그 보정이 건물의 원형 경계에서 잘리면서 화면 위치가 한 프레임에 크게 바뀌었다.

서버는 다음 경유점까지 남은 이동량을 사용한다. 틱 사이 직선이 건물을 만나면 첫 경유점까지만 이동한다. 클라이언트는 직접 입력이 끝나면 원소와 같은 시점의 서버 위치를 표시한다. 포즈와 원소 패킷의 도착 주기가 달라 포즈 기록을 별도로 보관하고, 카메라는 최근 확정 위치를 부드럽게 따라간다. [서버 이동](../../shared/Runtime/Sim/MovementSystem.cs) · [클라이언트 표시](../../client/Assets/_Project/Scripts/Runtime/Legion/MovementPrediction.cs)

| 맵 가장자리 건물, 동일 시드 | 수정 전 | 수정 후 |
| --- | ---: | ---: |
| 도착 전 가장 짧은 서버 틱의 이동량 | 0.082m | 0.347m |
| 60fps 간격으로 재생한 분대 중심의 최대 프레임 이동량 | 2.134m | 0.117m |

단일 건물, 맵 먼 쪽의 건물, 여러 건물과 30기 진형도 각각 도착했다. 서버 테스트는 틱당 이동 한도와 틱 사이 건물 비관통을 함께 검사한다. Unity 6000.6.0f1에서 컴파일과 EditMode 216개가 통과했다. 실제 프리팹으로 2인 시뮬레이션의 우회 위치를 60fps 간격으로 재생했으며, 분대 중심의 최대 프레임 이동량은 0.117m였다. [서버 측정](../../.artifacts/building-detour/server-after.txt) · [표시 측정](../../.artifacts/building-detour/client-after.txt) · [수정 전 위치 기록](../../.artifacts/building-detour/before/edge-display.csv) · [Unity 재생 결과](../../.artifacts/building-detour/editor-replay.json) · [전장 캡처](../../.artifacts/building-detour/corner.png)

.NET 솔루션 빌드는 오류 없이 끝났으며 NuGet 취약성 피드 조회 경고가 2개 있었다. 기본 권한에서 TLS 시험 인증서의 개인 키 접근이 거부됐다. 승인 권한에서 직렬 전체 테스트를 실행해 843개를 통과했다. [빌드 로그](../../.artifacts/building-detour/dotnet-build-final.log) · [테스트 결과](../../.artifacts/building-detour/building-detour-dotnet-final-elevated.trx)

실제 온라인 Play의 조작과 프레임 시간은 미검증이다.

2026-11-11에 OpenRA의 경로 구간 사이 이동량 전달과 Recast의 경로 보정 방식을 확인했다. 현재 원형 장애물 탐색과 충돌 검사를 재사용해 같은 틱에서 남은 거리만 처리했다. [OpenRA Move](https://github.com/OpenRA/OpenRA/blob/bleed/OpenRA.Mods.Common/Activities/Move/Move.cs) · [Recast 경로 처리](https://github.com/recastnavigation/recastnavigation/blob/main/DetourCrowd/Source/DetourPathCorridor.cpp)
