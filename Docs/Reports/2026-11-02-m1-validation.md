# M1 폴더 정리·접속·이동 구현 검증

2026-11-02. 사용자가 승인한 **폴더 규칙 문서화 → 기존 자산 이동·참조 확인 → M1 구현** 순서로 진행했다. [계획](../Plans/2026-11-02-m1.md), [폴더 규칙](../Rules/project.md), [프로토콜](../protocol.md)을 함께 참고한다.

M2 착수 시 사용자 보고로 **Unity Play 접속 확인**을 기록했다. 두 클라이언트 이동·보간의 수동 확인은 사용자의 요청에 따라 보류하며 M1을 완료 처리하지 않는다. 이후 시야·연무 구현과 v2 결과는 M2 보고서에서 구분한다.

## 구현

- `Assets/_Project` 기준을 문서화하고 Scenes·Editor·렌더링 설정·Input Actions를 Unity AssetDatabase로 이동했다. 기존 GUID와 참조를 유지하고 Build Settings의 이동 경로를 갱신했다.
- Runtime·Editor·EditMode 테스트 어셈블리를 분리했다. 새 게임 씬은 `Assets/_Project/Scenes/Legion.unity`, 연결된 프리팹은 `Assets/_Project/Prefabs/LegionClient.prefab`이다.
- 기존 LiteNetLib로 `--listen` UDP 서버와 Unity/콘솔 공용 `ClientConnection`을 구현했다. 버전·데이터 해시·시작 자연 원소를 확인하고 서버가 세션 소유자를 정한다.
- 신규 접속을 기존 시뮬레이션에 추가하고 실제 적용 입력 번호를 자기 Pose에 포함한다. 종료 시 입력을 멈추고 기존 군단·본진·자원을 유지한다.
- Units는 수신자 `WorldView`에서만 만들고 각 축 16비트 상대 좌표·64기 청크로 전송한다. 최신 틱의 청크 전체가 모여야 관측을 교체한다.
- 기존 Player/Move·UI 액션을 재사용했다. 내 군단 중심 예측·서버 보정과 다른 유닛의 지연 보간, 단일 ParticleSystem 표시, 카메라, uGUI 접속 화면을 연결했다.
- 한글은 Noto Sans KR 2.004의 수정하지 않은 OFL 원본에서 필요한 글리프를 정적 아틀라스로 생성했다. 새 UPM/NuGet 의존성은 없다. 고지는 [외부 자료](../third-party.md)에 기록했다.

## 환경과 실제 자동 검증

Unity **6000.6.0f1**, 연결된 Main Editor, Windows. 이 PC에는 SDK **10.0.302**가 있으며 저장소의 **10.0.401** 고정은 다른 PC의 설정이다. 사용자의 설명을 반영해 `global.json`은 유지했다.

설치 SDK를 직접 호출해 검증했다. 일반 `dotnet test`는 고정 SDK를 찾지 못하므로, 아래 결과를 정확한 10.0.401 검증으로 해석하지 않는다.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' 'C:\Program Files\dotnet\sdk\10.0.302\dotnet.dll' build server/ElementalLegion.sln --no-restore -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
& 'C:\Program Files\dotnet\dotnet.exe' 'C:\Program Files\dotnet\sdk\10.0.302\dotnet.dll' test server/ElementalLegion.sln --no-restore -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
```

- 변경 전 기준: xUnit **90/90 통과**.
- Release 전체 빌드: **경고 0, 오류 0**.
- Release xUnit: **106/106 통과**, 실패·건너뜀 0. 기존 M0 테스트와 최종 리뷰 회귀 2건을 포함한다.
- 실제 루프백 UDP: 동일한 Unity용 ClientConnection을 쓰는 두 peer의 접속·소유자 분리·이동·서로의 시야 진입, 데이터 해시 불일치 거절·종료 후 이동 정지를 확인했다. 임시 포트를 사용하고 테스트 종료 시 소켓을 정리한다.
- 순수 클라이언트 소스를 xUnit에 직접 연결해 입력 확인 번호에 따른 재적용, 오래된 Pose 무시, 정규화·월드 경계·256개 이력 제한, 유닛 보간·미래 샘플 유지·최신 관측에서 사라진 ID 제거를 확인했다.
- 패킷의 길이·추가 바이트·비유한 방향·버전·원소·해시, 양자화 오차, 역순 청크와 원자적 조립, 신규 세션의 초기 상태·소유자 슬롯 보존을 확인했다.
- Unity 실제 Editor 컴파일: **compilationFailed=false**, 오류 없음.
- Unity **EditMode 3/3 통과**: 공유 JSON 가져오기와 기존 입력 GUID/액션, Runtime의 Editor 의존성 배제, 프리팹 필수 참조·누락 스크립트·영속 UI 입력 참조, 시작 씬과 한글 데이터/UI 글리프를 검사했다.

없는 씬·프리팹으로 EditMode 2건이 먼저 실패한 뒤 자산을 만들었다. 이후 발견한 Input Actions 임시 참조의 저장 누락과 실제 원소명 `흙`의 글리프 누락을 같은 테스트로 재현하고 수정했다. 테스트용 asmdef의 Newtonsoft.Json 참조 누락도 실제 Editor 컴파일 결과로 확인·수정했다.

TMP 기본 자산 가져오기의 출처 확인 창은 사용자가 직접 처리했다. 외부 확인 창을 자동으로 우회하지 않았다. 생성 직후 Game View의 EditMode 캡처는 격자만 확인했으며 Overlay UI의 Play 시각 검사로 보고하지 않는다.

## 데이터와 기존 변경 보존

- 게임 데이터 SHA-256은 M0와 같은 `08fc75de1665c4ef6e940fa992610eadc0af7b593bd6472f3df47f7f2f9fd42b`다. 게임 밸런스와 새 placeholder 수치를 변경·추가하지 않았다.
- 기존 사용자 변경 6개를 시작 시 해시로 기록했다. 이동된 URP Global Settings, manifest·lock, ProjectAuditorSettings, ShaderGraphSettings는 내용 해시가 같다. EditorBuildSettings는 입력 설정을 유지한 채 이동 경로와 Legion 시작 씬을 반영했다.
- Unity가 URPProjectSettings에 `m_ProjectSettingFolderPath: URPDefaultResources`를 자동 기록했다. 렌더링 자산 GUID·파이프라인 선택을 임의 변경하지 않았다. QualitySettings에는 Git에서 표시할 내용 차이가 없고 줄바꿈 정규화 경고만 확인했다.
- 새 공유 C# 파일에 Unity `.meta`가 생성됐음을 확인했다. 자산 이동은 삭제 후 재생성이 아니라 GUID 보존 이동이다.
- 현재 체크아웃에서 작업했고 커밋·스테이징·푸시·브랜치 변경은 하지 않았다.

## 아직 실행하지 않은 확인과 M1 한계

- 저장소 작업 규칙에 따라 **Unity Play와 Windows Player의 실제 입력·카메라·Overlay UI·두 화면 이동 검사는 사람이 수행한다**. 컴파일·EditMode·UDP 검증을 이 수동 완료 조건으로 대체하지 않는다.
- Windows Player 빌드, 실제 인터넷/LAN 품질, 지연·유실 주입, 최대 부하의 네트워크 틱 p99·프레임·GC·클라이언트별 대역폭은 이번에 측정하지 않았다. M0 콘솔 성능 기록을 UDP 성능 근거로 사용하지 않는다.
- 시야 필터는 처음부터 적용했지만 안개 렌더링·탐색 기록은 M2, 전투·전향·드롭 이벤트 표현은 M3, 유령 만료는 M4, 계정 재접속·저장은 M5, 15피어 부하는 M6 범위다.
- M1은 계정 인증 없는 개발 접속이다. 종료한 군단·본진은 유지하고 소유자를 재사용하지 않으므로 프로세스당 최대 15개의 새 세션 후에는 개발 서버를 재시작한다.
- 자기 중심 예측은 월드 경계만 계산하며 건물 충돌은 서버 확정 위치로 보정한다. 숨겨진 건물을 클라이언트에 전송하지 않는다.

## 사람이 확인할 순서

1. README의 `--listen` 명령으로 UDP 서버를 실행한다.
2. Unity에서 `Assets/_Project/Scenes/Legion.unity`를 열고 사람이 Play를 누른다.
3. `127.0.0.1`과 자연 원소로 접속하고 방향키/WASD 입력, 카메라 추적, UI 조작 중 이동 차단, 종료·재접속 화면을 확인한다.
4. 두 번째 Unity 클라이언트 또는 Windows Player를 같은 서버에 접속한다. 첫 두 본진은 최소 간격을 유지하므로 서로 향해 이동해 시야에 들어온 뒤 상대 이동 보간을 확인한다.
5. 종료한 쪽의 군단이 멈추고 남아 있는지 확인한다. 수동 확인 전에는 M1의 최종 플레이 완료 조건을 통과했다고 표시하지 않는다.

## 최종 리뷰

독립 읽기 전용 코드 리뷰 1회를 수행했다. Critical은 없었고 Important 1건과 CLI 안내 문구 1건을 확인했다.

- **Important 수정:** 기존 관측 핵 30개·유닛 0개인 월드에 새 플레이어를 추가하면 예약 버퍼를 현재 유닛 수로 축소하다 예외가 발생했다. `JoiningWithMoreVisibleDropsThanUnitsPreservesTheExistingWorld`에서 `ArgumentOutOfRangeException`을 먼저 재현했다. 시뮬레이션·전투·반응·경제의 예약을 기존 용량을 유지하는 증가 방식으로 바꾸고, 전투 후 생존 유닛·핵·틱 보존 테스트도 추가했다. 최종 전체 106/106 통과를 확인했다.
- 기본 실행에서 UDP가 미구현이라고 하던 안내를 실제 `--listen` 명령으로 정정하고 콘솔 실행 출력을 확인했다. 단순 안내 문구에는 구현을 그대로 따라가는 별도 테스트를 추가하지 않았다.
- 수정 뒤 독립 재리뷰를 수행했다고 주장하지 않는다. 재현 테스트·전체 테스트·컴파일로 확인했다.

검토에서 별도 판단한 범위는 다음과 같다.

| 판단 | 이유와 영향 |
| --- | --- |
| 열린 현재 체크아웃에서 진행 | 승인한 자산 정리 대상이 이 Editor이므로 다른 worktree를 대신 조작하지 않았다. 기존 사용자 변경은 해시·참조로 보존했다. |
| SDK 고정 유지, 설치 SDK로 검증 | 다른 PC의 설정을 임의 변경하지 않았다. 정확한 10.0.401 실행은 여전히 미확인이다. |
| 승인한 순서를 같은 세션에서 실행하고 Git 상태 유지 | 추가 실행 방식 확인이나 커밋 없이 결과를 검토할 수 있다. 변경은 현재 작업 트리에 남아 있다. |
| 종료한 소유자·월드 개체 보존 | 데이터 보존을 우선했다. 유령 만료·계정 재접속 전까지 실행당 15개 새 세션 한계가 있다. |
| MTU 크기 청크와 완성 프레임 교체 | 비신뢰 전송에 큰 패킷 단편화를 기대하지 않는다. 청크 유실 시 이전 완성 관측을 유지한다. |
| 계정 인증·암호화와 M2~M6 후속 기능 | 현재는 개발용 M1이다. 이 보고서의 자동 검증을 배포 보안·안개·전투 연출·최대 네트워크 부하 검증으로 해석하지 않는다. |
| Play·Player의 최종 확인은 사람 수행 | 저장소 규칙을 따른다. 실제 UI 조작·시각 동작·보간 체감은 미확인이다. |
| 지연 입력이 한 서버 틱에 모이는 보정 체감 | 이번 리뷰에서 독립 결함으로 확정하지 않았다. 순수 단위 테스트는 실제 시간축의 예측/서버 통합 품질을 보장하지 않으므로 사람의 Play·후속 지연 테스트에서 확인한다. |
| 사용자 설정의 기존 변경은 리뷰 수정 대상에서 제외 | 요청 범위를 보존한다. 기존 렌더링·도구 설정 전체를 품질 감사한 것은 아니다. |
