# 플레이어가 여는 인터넷 방 검증

## 실행과 배포

배포자가 중계 서비스의 HTTPS 주소를 `Bootstrap` 씬에 설정하고 Windows x64 게임을 빌드한다. 빌드 완료 단계에서 방장 서버와 .NET 런타임을 `<게임명>_Data/Server`에 넣는다. 두 플레이어는 같은 설치본을 사용한다. 방장은 **방 만들기 → 인터넷**에서 방 제목과 선택 비밀번호를 설정한다. 친구는 **방 찾기 → 인터넷**에서 제목을 누른다. [배포 절차](../../README.md#온라인-서버-운영)

중계는 방장 서버가 목록 등록을 확인하기 전까지 방을 보여 주지 않는다. 방장과 참가자는 각자 중계에 연결한다. 전투·시야·대기실 판정은 방장 PC에서 실행하며, 중계가 방별로 연결을 짝짓는다. 방장 서버가 종료되면 참가자의 연결과 방 목록을 정리한다. 플레이어 호스트의 전장은 해당 프로세스의 수명을 따르며, 서버가 살아 있는 동안 기존 계정으로 복귀할 수 있다.

## 보안 경계

- 잠긴 방은 ASCII 숫자 4~8자리를 문자열로 비교한다. 방 목록에는 잠금 여부만 보인다. 비밀번호 다이제스트는 방마다 분리해 메모리에 보관한다. 분당 잘못된 시도는 방마다 계정 5회, IP 10회, 전체 20회로 제한한다.
- 등록 키, 입장권, 참가자별 브리지 키를 따로 발급한다. 참가자의 계정 키는 중계에서 검증한다. 게임 서버를 실행할 때 등록 키는 표준 입력으로 전달한다. 중계와 게임의 외부 연결은 HTTPS/WSS를 사용한다.
- 운영 서버 방과 플레이어 방을 합쳐 프로세스당 4개까지 허용하고, 계정당 만들고 있는 방은 하나로 제한한다. 방장 계정이 30초 안에 참가자 연결까지 완료해야 방을 계속 유지할 수 있다. WebSocket 상한은 전체 144개이며 IP별로 제어·입장 20개와 호스트 브리지 20개를 별도로 제한한다. 한 참가자가 느려도 다른 연결의 패킷을 함께 대기시키지 않는다.

플레이어 호스트는 전장 데이터를 자신의 PC에서 처리한다. 이 방 유형은 신뢰하는 친구끼리의 테스트를 기준으로 한다.

## 검증 범위

- .NET Release 빌드는 오류 없이 완료했고 전체 테스트 231개가 통과했다. NuGet 취약성 데이터 조회는 소스 연결 오류로 `NU1900` 경고가 남았다.
- 루프백 TLS에서 방장 런타임과 중계, 참가자 연결을 함께 실행한다. 열린 방과 선행 0을 포함한 숫자 비밀번호 방, 비밀번호 누락·오입력·횟수 제한, 준비와 방장 시작, 게임 명령 응답, 같은 플레이어 ID 재입장, 계정 폐기와 목록 정리를 확인한다.
- 공식 .NET 런타임 팩으로 자체 포함 서버를 만들어 테스트 출력 폴더에 배치하고 `ElementalLegion.Server.exe` 실행을 확인했다. Windows의 자식 프로세스에서 표준 입력이 오지 않거나 잘못됐을 때 제한 시간 안에 실패하는 것도 검사했다.
- Unity 프로젝트의 Shared, Client, Editor, EditModeTests C# 어셈블리를 실제 Unity 참조로 컴파일한다. Unity Play와 Windows Player의 화면·입장, 두 외부망에서의 연결 및 중계 배포는 별도 검증이다.

기존 대기실·복제 코드와 `ClientWebSocket`, ASP.NET Core WebSocket을 재사용했다. 일반 리버스 프록시의 도달 가능한 목적지 대신 방장 PC가 먼저 연결해 둔 WebSocket을 짝지어 사용한다. [ASP.NET Core WebSocket](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0) · [리버스 프록시 직접 전달](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/direct-forwarding?view=aspnetcore-10.0) · [Unity 빌드 후 콜백](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Build.IPostprocessBuildWithReport.html)
