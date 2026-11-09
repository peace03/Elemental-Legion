# 네트워크 프로토콜 버전 17

로컬은 LiteNetLib **1.3.5**, 인터넷은 HTTPS와 WSS를 사용한다. 게임 메시지는 두 전송 방식 모두 little endian 바이너리이며 **1024B 이하**다. `u8/u16/u32/u64`, `i16/i32/i64`, `f32`를 사용한다. 좌표는 XZ 평면의 미터다. 게임 수치 원본은 JSON이다.

## 연결과 명령

- UDP Hello에서 version **17**, LF 정규화 UTF-8 gamedata SHA-256, 허용된 시작 자연 원소, 닉네임을 검사한다. 이전 버전은 VersionMismatch로 거절하고 닉네임이 유효하지 않으면 InvalidNickname으로 거절한다.
- 닉네임은 UTF-8로 1~48B, 최대 16자다. 한글을 포함한 기본 다국어 평면의 글자와 십진 숫자, ASCII 공백·`-`·`_`를 허용한다. 글자나 숫자가 하나 이상 있어야 하며 맨 앞과 뒤의 공백, 보이지 않는 한글 채움 문자는 허용하지 않는다. UTF-8 오류나 잘린 이름은 거절한다. 닉네임은 표시용이며 중복될 수 있다. 서로 다른 세션은 서버가 발급한 플레이어 ID로 구분한다.
- 클라이언트는 접속 중 첫 Welcome 또는 LobbyState를 기다린다. LobbyState를 받으면 대기실을 열고, 실제 전장은 Welcome 이후에 표시한다.
- 서버가 연결에 양수 playerId를 배정한다. 유령 복귀에는 기존 ID를 쓰고 새 입장에는 새 ID를 쓴다. 복귀 시 명령 순번과 복제 상태를 초기화한 뒤 현재 시야와 탐색을 다시 보낸다. Build는 격자 칸을 의도로 보내고 서버가 실제 위치를 계산한다. 전투 명령은 하나의 증가하는 sequence와 JSON의 수신 제한·큐를 공유한다.
- Move는 유한한 방향을 안전하게 정규화한다. 분대는 야전 1~3과 수비대 4이며 마스크의 bit0~3이 각각 대응한다. 전체 야전 선택은 7이다. MoveTo는 유한한 맵 내부 지점만 받는다. Transfer는 본진 범위 안에 합류한 자기 병력만 옮기고, Rally는 목적 야전 분대와 수량을 받는다(0은 기존 호출의 대기 병력 전원). Focus는 자기 분대 1~3 또는 본진 4 중 하나만 받는다. Train은 유효한14원소이며, 모든 명령의 sequence는0보다 커야 한다. 중복·역행 입력은 거절한다.
- 접수 결과와 실제 실행 결과는 구분한다. Accepted는 접수만 뜻한다. 실행 실패는 Gameplay의 CommandRejected, Build/UpgradeBase/CancelTraining 실행 성공은 CommandApplied로 본인에게 전달한다.
- 양성 주문 ID는 플레이어별 `NextTrainingOrderId`에서 발급하며 입력 명령의 sequence와 독립적이다. 취소 요청은 0이 아닌 주문 ID를 보내고, 서버가 실행 시점에 본인 대기열의 대기 주문인지 확인한다. 접수 후 먼저 완료되거나 취소된 주문은 실행 결과로 거절하며, 정상 접수된 미적용 취소 명령은 승계할 수 있다.
- Pose.appliedSequence까지의 **이동 입력만** 예측에서 제거·재적용한다. Train/Rally를 이동 이력에 넣지 않는다. 이미 관측한 건물은 공유 선분–원 헬퍼로 중심을 예측하고 숨겨진 장애물은 서버 보정으로 처리한다.
- 종료 시 세션 복제·시야·탐색·보간·효과를 초기화한다. 새 Baseline 전에는 전체 미탐색이다. 유령이 유지되는 동안 슬롯을 점유하며 만료나 본진 파괴 뒤 슬롯을 재사용한다. UDP는 LiteNetLib 정상 연결 종료의 추가 데이터 1바이트로 패배(1) 또는 승리(2)를 알린다. WSS는 SessionEnded의 reason으로 같은 값을 보낸다.

## 메시지와 채널

디스크 저장은 파일 래퍼 `Format=2`와 `WorldCheckpoint.SchemaVersion=2`를 사용한다. 양성 주문·지불 비용·시작/완료 틱·다음 주문 ID를 보존하고, 디스크 재시작 후 유령 상태에서는 양성을 정지한다. 같은 계정이 복귀하면 정지한 틱만큼 타이머를 옮겨 남은 시간을 이어 간다. 자발적 로그아웃의 주문 취소·환불과 호스트 승계의 정확한 틱 복원은 별도 경로다. v1 파일이나 다른 데이터 해시는 일반 로더로 읽지 않으며, 명시적인 이관 도구를 거쳐야 한다.

UDP 채널0은 대기실·명령·응답 및 비신뢰 위치, 채널1은 신뢰 시야·표시 해제, 채널2는 신뢰 자기 상태·정적 관측·전투 결과, 채널3은 채팅이다. UDP 신뢰 메시지는 ReliableOrdered다. WSS는 같은 코덱을 쓰고 신뢰 메시지의 순서를 보존한다. 아직 보내지 않은 위치 데이터는 최신 묶음만 보관한다.

WSS 입장권 인증 메시지는 원본 32B다. 인증 뒤 게임 프레임은 `generation:u64`와 기존 게임 본문으로 구성하며 최대 1032B다. 중계 판정 방은 세대 0, 플레이어 방은 세대 1부터 사용한다. 이전 세대의 입력·관측은 적용하지 않는다. `/bridge`의 서버 연결은 중계가 세대에 묶어 관리하고 기존 1024B 이하 본문을 사용한다.

| ID | 메시지 | 필드 순서 | 크기/전송 |
| --- | --- | --- | --- |
| 1 | Hello C→S | kind:u8, version:u16, dataHash:32 bytes, startElement:u8, nickname, 선택 flags:u8, 선택 resumeKey:32B, 선택 hostProof:32B |UDP 연결 요청|
| 2 | Welcome S→C |kind:u8, playerId:i32, roomPlayers:u8, 선택 resumed:u8, keyLength:u8, key:0/32B|6/8/40B 채널0 신뢰|
| 3 | Move C→S |kind:u8, sequence:u64, squadMask:u8, directionX:f32, directionZ:f32|18B 채널0 신뢰|
| 4 | Pose S→C |kind:u8, tick:i64, playerId:i32, count:u8, (squadId:u8, appliedSequence:u64, centerX/Z:f32×2, directionX/Z:f32×2, movementRevision:u32)×count|14B +29B×1~4, 채널0 Unreliable|
| 5 | Units S→C |kind:u8, tick:i64, recipient:i32, anchorX:f32, anchorZ:f32, total:u16, part:u16, visionRevision:u32, records|29B +29B×최대34, 채널0 Unreliable|
| 6 | Rejected S→C |kind:u8, sequence:u64, admission:u8|10B 채널0 신뢰|
| 7 | FogBaseline S→C |kind:u8, tick:i64, recipient:i32, revision:u32, baseRevision:u32, totalBytes:i32, offset:i32, body|29B +최대995B 채널1|
| 8 | FogDelta S→C |FogBaseline과 같은 헤더, 변경분 body|채널1|
| 9 | Forget S→C |kind:u8, tick:i64, recipient:i32, revision:u32, count:u16, (entityKind:u8,id:i32)×count|19B +5B×최대201, 채널1|
|10|Train C→S|kind:u8, sequence:u64, element:u8|10B 채널0 신뢰|
|11|Rally C→S|kind:u8, sequence:u64, targetSquad:u8, count:i32|14B 채널0 신뢰|
|12|Gameplay S→C|kind:u8, tick:i64, recipient:i32, visionRevision:u32, batch:u32, totalBytes:i32, offset:i32, body|29B +최대995B 채널2|
|13|Accepted S→C|kind:u8, sequence:u64|9B 채널0 신뢰|
|14|Build C→S|kind:u8, sequence:u64, buildingKind:u8, cellX:i32, cellZ:i32, towerElement:u8|19B 채널0 신뢰|
|15|UpgradeBase C→S|kind:u8, sequence:u64|9B 채널0 신뢰|
|16|CancelTraining C→S|kind:u8, sequence:u64, orderId:u64|17B 채널0 신뢰|
|17|ChatSend C→S|kind:u8, global:u8, text|채널3 신뢰|
|18|ChatMessage S→C|kind:u8, id:u64, owner:i32, global:u8, anchorId:i32, nickname, text|채널3 신뢰|
|19|ChatReport C→S|kind:u8, id:u64, reason|WSS 전용|
|20|LobbyReady C→S|kind:u8, startElement:u8, ready:u8|채널0 신뢰|
|21|LobbyStart C→S|kind:u8|채널0 신뢰|
|22|LobbyState S→C|kind:u8, selfIndex:u8, count:u8, minimum:u8, capacity:u8, aiCount:u8, aiDifficulty:u8×aiCount, aiColorId:u8×aiCount, (element:u8, ready:u8, colorId:u8, nickname)×count, title|채널0 신뢰|
|23|RoomDiscovery C→S|kind:u8, version:u16, nonce:u32|LAN 비연결 검색|
|24|RoomListing S→C|kind:u8, version:u16, nonce:u32, port:u16, count:u8, minimum:u8, capacity:u8, running:u8, title|LAN 비연결 응답|
|25|SessionEnded S→C|kind:u8, reason:u8|WSS 종료, 패배 1 / 승리 2|
|26|LobbyError S→C|kind:u8, error:u8|채널0 신뢰|
|27|JoinError S→C|kind:u8, failure:u8|UDP 승인 후 비동기 계정 검증 실패로 종료할 때의 추가 데이터|
|28|LobbyAiCount C→S|kind:u8, aiCount:u8|채널0 신뢰|
|33|LobbyAiDifficulty C→S|kind:u8, aiIndex:u8, difficulty:u8|3B 채널0 신뢰|
|34|LobbyColor C→S|kind:u8, colorId:u8|2B 채널0 신뢰|
|35|MoveTo C→S|kind:u8, sequence:u64, squadMask:u8, destinationX/Z:f32×2|18B 채널0 신뢰|
|36|Transfer C→S|kind:u8, sequence:u64, fromSquad:u8, targetSquad:u8, count:i32|15B 채널0 신뢰|
|37|Focus C→S|kind:u8, sequence:u64, anchor:u8|10B 채널0 신뢰|

Build의 towerElement는 선택한 원소 인덱스다. 자연·에너지 타워는 해당 그룹의 인덱스가 필수이며 다른 시설은 255를 보낸다. 자기권은 cellX와 cellZ를 0으로 보내면 서버가 본진 중심을 사용한다.

문자열은 LiteNetLib 형식이다. `utf8BytesPlusOne:u16` 뒤에 지정된 UTF-8 바이트가 이어진다. 길이 필드에서 1을 뺀 값이 실제 UTF-8 바이트 수다. 방 제목은 한 줄 최대 32자다. 채팅은 시야 밖 발신자의 `anchorId`를 0으로 보낸다. 전체 채팅도 0이다. 발신 좌표는 메시지에 들어가지 않는다.

Hello flags의 bit0은 기존 계정 키, bit1은 로컬 서버 프로세스에 비공개로 넘긴 방장 증명, bit2는 계정 사용 요청이다. bit0과 bit1에 대응하는 고정 길이 값은 그 순서대로 붙인다. 방장은 LobbyState의 첫 참가자다. LAN 검색 응답의 주소는 데이터가 들어온 소켓에서 얻는다. UDP 계정 키 교환과 LAN 방 입장은 신뢰하는 로컬 네트워크에서 사용한다.

`LobbyState.count`와 `selfIndex`는 사람 참가자 목록 기준이다. 대기 중인 방의 검색 인원은 사람과 AI를 합산한다. 방장만 시작 전에 `LobbyAiCount`로 AI 수를 정할 수 있으며, 합이 정원을 넘으면 `LobbyError.RoomFull`(6)로 거절한다. 사람 참가자가 모두 준비하고 합이 최소 인원에 도달하면 시작할 수 있다. AI가 포함된 방에서 마지막까지 살아남은 플레이어에게 승리를 보낸다.

`aiDifficulty`와 `difficulty`는 쉬움 0, 보통 1, 어려움 2다. `LobbyAiDifficulty.aiIndex`는 AI 목록 안의 0부터 시작하는 순번이며, 사람 참가자 수를 더하지 않는다. 방장만 대기 중인 AI의 난이도를 변경할 수 있다. 존재하지 않는 순번은 `InvalidState`, 0~2 밖 값이나 잘린 명령은 `InvalidPacket`, 다른 참가자의 변경은 `NotHost`로 거절한다. AI 추가 시 보통을 지정하고 마지막 AI부터 제거하며, 남은 AI의 선택은 유지한다. 난이도 목록과 AI별 판단 상태는 서버 승계에도 포함한다. v15 이하 클라이언트·서버와 혼용할 수 없다.

`colorId`는 공유 `PlayerPalette`의 0~14 ID다. 사람과 AI 전체에서 유일해야 하며, 새 참가자와 추가 AI는 서버가 남은 색을 자동 배정한다. `LobbyColor`는 인증된 자기 참가자만 변경한다. 다른 사람이 쓰는 색은 `ColorTaken`(7), 준비 완료 뒤 변경은 `InvalidState`(4), 범위 밖 값·잘림·추가 바이트는 `InvalidPacket`(5)로 거절한다. 사람이 AI 색을 선택하면 해당 AI를 남은 색으로 재배정한다. 복구 중에는 변경을 적용하지 않으며 시작 뒤에는 색을 고정한다. AI 색 목록과 사람의 선택은 대기실 승계 백업에 포함한다.

## 온라인 방 접속

- `POST /accounts`는 `{ nickname }`을 받아 32바이트 계정 키를 16진수 `token`으로 반환한다. `GET /rooms`는 방의 `id`, `title`, `count`, `minimumPlayers`, `capacity`, `running`, `persistent`, `playerHosted`, `passwordRequired`를 `rooms` 배열로 보낸다. 목록은 중계 주소 하나에서 제공하며 방장 주소와 비밀번호는 포함하지 않는다.
- `POST /rooms`에는 `{ title, minimumPlayers, capacity }`, `POST /rooms/{roomId}/tickets`에는 `{ version, dataHash, startElement, password? }`를 보낸다. 두 요청은 `Authorization: Bearer <token>`으로 인증한다. `startElement`는 상시 월드 새 입장에 필요하다. 응답의 `ticket`은 32바이트 16진수다.
- 같은 도메인의 `wss://…/rooms/{roomId}/play`에 연결한 뒤 첫 바이너리 메시지로 입장권 원본 32바이트를 보낸다. 서버는 발급 후 30초 동안 한 번만 받아들이며 계정·방·복귀 자격을 검사한다. 인증 전에는 방 데이터를 보내지 않는다. WSS 압축은 사용하지 않는다.
- 온라인 전송은 TCP 위에서 동작한다. 전송 대기 중인 위치 묶음은 최신 것만 유지한다. 명령 결과와 시야 변경의 순서를 유지하며, 신뢰 큐나 전송 제한을 넘긴 연결은 종료한다. Windows Player 지연과 외부망 검증은 공개 전 별도 기준을 따른다. [M5 계획](Plans/2026-11-03-m5-online-rooms.md)

### 플레이어가 여는 인터넷 방

- 인증된 `POST /hosted-rooms`에 `{ title, minimumPlayers, capacity, password, version, dataHash }`를 보내면 `{ room, hostKey }`를 받는다. `password`는 빈 값 또는 ASCII 숫자 4~8자리 문자열이다. 선행 0도 비밀번호에 포함된다. 중계는 방별로 분리한 다이제스트를 메모리에 보관하고 잘못된 시도 횟수를 제한한다. 잠긴 방은 올바른 비밀번호를 검증한 뒤 입장권을 발급한다. 방을 만든 계정은 자신의 방에 자동으로 입장할 수 있다.
- 클라이언트는 동봉한 서버를 `--host-room`으로 실행하고 표준 입력으로 `{ serviceUrl, roomId, hostKey }`를 전달한다. 방장 서버는 `/rooms/{roomId}/host`에 먼저 `hostKey` 원본 32바이트를 보내고 방 정보·버전·데이터 해시·세대를 검증한다. 상태 메시지는 `kind`, `count`, `running`, `tick`, `generation`을 포함한다. 중계의 목록 등록 확인을 받으면 자식 프로세스가 `HOST_READY`를 출력한다.
- 입장권을 교환한 참가자가 연결되면 중계는 방장 서버에 그 참가자 전용 브리지 키를 보낸다. 방장 서버는 `/rooms/{roomId}/bridge`에 같은 원본 32바이트 키로 접속한다. 중계는 이 연결과 참가자의 `/play`를 묶어 1024B 이하의 기존 바이너리 메시지를 양방향 전달한다. 실제 게임 판정과 시야 필터는 방장 서버에서 실행한다. 게스트의 계정 키는 방장 서버에 전달하지 않는다.

### 인터넷 방 서버 승계

| ID | 메시지 | 게임 본문 |
| --- | --- | --- |
| 29 | HostCapability C→중계 | kind:u8, capable:u8 |
| 30 | HostStandbyOffer 중계→C | kind:u8, roomId:string, key:string, generation:u64 |
| 31 | HostLeave C→중계 | kind:u8 |
| 32 | HostMigration 중계→C | kind:u8, phase:u8, generation:u64, tick:i64, isHost:u8, backupAgeMs:i32, reason:string |

- phase는 None0/Preparing1/Recovering2/Resyncing3/Failed4다. backupAgeMs는 중계의 검증 백업 수신 후 경과 시간이며 백업이 없으면 -1이다. 승계 제어는 중계만 보낸다. 방장 서버와 참가자가 위장한 제어 메시지는 거절한다. 프레임의 세대 변경은 중계가 보낸 확정 알림에서 수행한다.
- 후보는 일회용 실행 권한으로 `/rooms/{roomId}/standby`에 연결한다. 표준 입력에 `standby:true`, `generation`을 추가하고 준비 뒤 `STANDBY_READY`를 출력한다. 단순 예비 상태에서는 전체 월드 데이터를 받지 않는다.
- 현재 서버의 `/migration/upload`와 확정된 후보의 `/migration/download`는 해당 방의 제어 권한으로 인증한다. 첫 메시지는 원본 32B 권한이며, 이후에는 길이·SHA-256을 담은 최대 256B JSON 헤더, 최대 32KiB 바이너리 청크, 빈 바이너리 종료 메시지를 주고받는다. 압축본은 8MiB, 해제 후에는 32MiB 이하이며 업로드 확인은 `OK`다. 업로드 연결은 다음 백업에도 재사용한다. 다운로드 연결도 예열·최종 복원에서 재사용하며 후보가 `NEXT` 텍스트를 보낼 때마다 중계가 해당 복원 요청의 일회용 권한을 확인하고 소비한다. 요청 사이 대기는 후보 제어 세션 수명에 묶이고, 각 전송에는 5초 제한을 적용한다.
- 제어 연결의 `backup`, `freeze`, `restore`, `activate`, `resume`, `retire`로 상태 이전을 조정한다. `restore`에는 `snapshotGeneration`, 현재 접속 계정 배열 `accounts`, 새 방장의 `creatorId`·`creatorNickname`을 담는다. 호스트는 최종 백업 업로드 확인 뒤 `checkpoint`를 보내고 후보는 실제 월드를 복원한 뒤에만 `readyForActivation`을 보낸다. `activate`는 준비한 참가자 중 그사이 끊긴 계정의 로그아웃을 반영하고 틱을 시작한다. 중계는 검증된 후보 하나에만 새 세대를 부여한다. 남은 참가자의 새 bridge 연결까지 확인하며, 세대 확정 이후 실패하면 이전 서버를 재개하지 않고 다음 세대로 복구한다. 서버 연결의 정리는 참가자의 `/play` 연결 수명과 분리된다.
- 정상 퇴장에서는 최종 틱과 대기 명령을 이전하고 퇴장자에게 `left`를 보낸다. 강제 종료는 최신 검증 백업을 사용해 틱이 되돌아갈 수 있다. 클라이언트는 새 세대의 시야·자기 상태·위치·유닛 기준 상태가 준비되면 한 번에 표시한다. 경제 명령은 자동 재전송하지 않는다.
- 마지막 사람 참가자가 나가면 AI 수와 관계없이 방·백업·입장권을 정리한다. 중계 종료나 복구 제한 초과도 종료 사유를 전달한다. 이전 호스트 세대는 새 방 상태를 게시하거나 제거할 수 없다.

CancelTraining의 orderId는 소유자별로 발급하는 양성 주문 번호다. 서버는 해당 소유자의 대기열에서 아직 양성이 시작되지 않은 예약만 취소한다.

Welcome의 `roomPlayers`가 0이면 기본 맵 크기를 쓴다. 2 이상이면 서버가 게임 시작 시 확정한 플레이어와 AI의 합으로 맵 크기를 계산한다. 복귀에서도 같은 값을 보낸다. Welcome보다 먼저 도착한 복제 패킷은 제한된 버퍼에 보관했다가 크기를 적용한 뒤 처리한다.

Units record는 `id:i32, owner:i32, element:u8, relativeX:i16, relativeZ:i16, flags:u8, health:f32, shield:f32, statusMask:u16, squadId:u8, movementRevision:u32`이다. owner=-1과 squadId=0은 중립이다. flags bit0=수비대/bit1=전향됨/bit2=유령/bit3=합류 중이며 나머지는0이다. 합류 중 병력의 목적 분대는 자기 상태에만 포함하고 적 관측에는 전송하지 않는다. 상태 bit0..6=Wet/Chill/Burn/Radiation/Freeze/EarthMark/Smoke, bit7=Haste, bit8=Steam, bit9=Sand이다.

- 상대 좌표는 `round((position-anchor)/mapSize*32767)`다. 축별 최대 오차는 `mapSize/65534`m이며 디코딩 반올림 경계만 보정한다.
- 기술 상한4096기, 청크당34기, 최대121청크다. 64비트 조각 마스크에 의존하지 않는다. 빈 관측도 헤더1청크다.
- 동일 tick/recipient/anchor/total/revision의 모든 조각이 검증된 뒤 원자 반영한다. 잘림·추가 바이트·잘못된 인덱스/값·중복 완성·과거 프레임은 적용하지 않는다. 조각 역순과 내용이 같은 중복은 허용한다.
- 미래 시야 revision의 Units는 최신 완성 프레임 하나만 대기한다. 과거 revision은 폐기한다. Forget 이하 틱은 복원하지 않으며 새 재관측만 허용한다. 전향 결과는 ID를 유지하며 과거 소유자·체력 복원을 막고 최종 사망 ID는 다시 표시하지 않는다.

## 시야와 종류별 표시 해제

- 지형 격자의 한 변은 `ceil(mapSize/visionCellSize)`다. 원형 시야 또는 군단 카메라 시야와 겹치는 칸을 현재 시야로 표시하고, 지금까지 본 칸을 누적 탐색으로 보관한다. 유닛·사건은 서버에서 원 거리와 카메라 직사각형으로 정확히 검사한다.
- 칸 번호는 x+z×width, 비트는 LSB부터다. Baseline body는 현재 packed bitmap 뒤에 누적 탐색 bitmap이다. 기본 1680m 맵에서는 7056B이며, 2인 방에서는 144B다. 총 초기 비용에는 별도의 Gameplay도 포함되므로 실제 플레이어별 보고를 사용한다.
- Delta는 `count:i32, (cellIndex:i32,flags:u8)×count`, 오름차순·중복 없음. flags는0=미탐색,2=탐색,3=현재 시야,1은 금지다. 탐색은 연결 안에서 감소하지 않는다.
- 최초 revision1/base0, 변경 때만+1/base직전이다. offset은995의 배수다. 동일 revision/tick/owner/kind/total과 마지막 길이·모든 셀을 검증한 뒤 적용한다. 미완성·base 불일치로 현재 표시를 바꾸지 않는다.
- entityKind는 Unit0/Core1/Building2/Area3이다. 건물·영역은 별도 ID 공간으로 안정적인 ID를 유지한다. Forget은 이전에 관측한 종류별 ID 중 사라진 것만 포함한다. 좌표·체력·시야 밖 사건은 없다. 같은 채널에서 시야 변경분보다 먼저 보낸다.
- 핵·건물·영역도 Forget 틱 이하의 대기 프레임에서 복원하지 않는다. 지속 영역은 유효 관측 안에 있는 동안 유지하고 만료·시야 이탈·재관측을 구분한다. 파괴된 건물은 종류별 Forget으로 표시와 충돌 예측에서 제거한다.

## Gameplay body

공간적으로 움직이지 않는 관측과 중요 결과는 채널2에서 함께 보낸다. 자기 상태에는 상대의 경제·양성이 없다. count는 i32이며 record는 아래 고정 순서다.

| 부분 | 필드/기술 상한 |
| --- | --- |
|자기 상태333B|기존100B(baseX/Z:f32×2, mainElement:i32, baseLevel:i32, cores:i64, population:i32, reserved:i32, cap:i32, garrison:i32, rallyingCount:i32, trainingElement:i32, trainingStartTick:i64, trainingEndTick:i64, trained:i64, converted:i64, garrisonArrivals:i64, ownBuildingCount:i32, battleWins:i64), focusAnchor:u8, 분대 레코드58B×4|
|양성 대기열|예약 인구가 양수일 때 (orderId:u64,element:u8)×(reserved−1), 접수 순서. reserved는 진행 중인 1기를 포함하며 예약이 없으면 0|
|구성|공유 원소 순서별 count:i32×14; 합은 population|
|접속 순위|count:i32, (playerId:i32,score:i64,colorId:u8,nickname)×count; 레코드 16~63B, 최대 server.maxPlayers|
|소유 색|count:i32, (playerId:i32,colorId:u8)×count; 레코드 5B, 최대 server.maxPlayers|
|건물|count:i32, (id:i32,owner:i32,kind:u8,position:f32×2,radius:f32,hp:f32,maxHp:f32,towerElement:u8,ghostEndTick:i64,constructionEndTick:i64)×count; 레코드 46B, 최대1024|
|핵|count:i32, (id:i32,amount:i32,position:f32×2,endTick:i64)×count; 최대4096|
|영역|count:i32, (id:i32,reaction:u8,position:f32×2,radius:f32,endTick:i64)×count; 최대4096|
|사건|count:i32, event71B×count; 최대8192|

분대 레코드58B는 `id:u8, center:f32×2, direction:f32×2, heading:f32×2, destination:f32×2, order:u8, appliedSequence:u64, joined:i32, reinforcing:i32, reserved:i32, movementRevision:u32`다. ID 1~4 순서로 자기 분대만 보낸다. joined와 reinforcing의 전체 합은 population, reserved의 합은 예약 인구와 같아야 한다. order는 Hold0/MoveDir1/MoveTo2다. 적의 목적지·명령·예약 수량은 전송하지 않는다.

Pose는 자기 분대 1~4를 한 패킷에 묶어 WSS의 최신 위치 한 칸에서 분대별 상태가 덮어쓰이지 않게 한다. `TryReadPose`는 전체 묶음을 검증한 뒤 첫 레코드를 반환하고, 실제 클라이언트 연결은 `TryReadPoses`로 모든 레코드를 처리한다. 선택 변경 시 예측 기준을 새 분대로 바꾸며, 합류 중인 병력이나 다른 분대의 모델과 진형 표시에 보정값을 더하지 않는다. 이동 revision이 바뀌면 과거 위치와 보간하지 않는다.

event71B는 `kind:u8, owner:i32, sourceId:i32, targetId:i32, position:f32×2, amount:f32, detail:i32, commandSequence:u64, element:i16, hasSourcePosition:u8, sourcePosition:f32×2, entityKind:u8, entityId:i32, eventTick:i64, serial:u64, sourceKind:u8, targetKind:u8`다.

- eventKind는 CommandRejected0, Attack1, Reaction2, Death3, Converted4, CoreDropped5, CorePickedUp6, Trained7, Healed8, ShieldPulse9, Impact10, CommandApplied11, BaseUnderAttack12다. CommandApplied의 detail은 Build3, UpgradeBase4 또는 CancelTraining5다.
- BaseUnderAttack은 실제 시설 피해를 입은 소유자에게만 전달한다. position은 자기 피격 시설, amount는 실제 피해다. sourceId·targetId·entityId·detail·sequence는0, element는-1, hasSourcePosition은0, sourcePosition은(0,0)이다. 같은 본진 구역의 경보는 JSON의 100틱 동안 모으며 소유자별 경보 시점을 저장·승계한다. 소리와 화면 안내는 이 사건만 사용하며 공격자의 좌표·방향을 추정해 표시하지 않는다.
- 건물 kind는 Base0/NatureForge1/EnergyForge2/Collector3/NatureTower4/EnergyTower5/Dome6이다. 타워 이외의 원소 필드는 255다. ghostEndTick은 유령이 아닌 경우 0이다. reaction은 JSON의 6반응 순서다.
- 건설 중인 시설은 시야 필터를 통과한 경우에만 전송한다. constructionEndTick은 완공 시 0이며 Build의 CommandApplied는 착공을 뜻한다.
- 소유 색 목록은 접속 순위에 이미 공개된 소유자와 해당 수신자의 실제 유닛·건물 관측에 필요한 소유자만 포함한다. 유령도 관측된 경우에만 추가하며, 숨겨진 유령의 존재나 위치를 공개하지 않는다. ID는 오름차순이며 소유자 ID와 색은 각각 중복되지 않는다. ID가 15를 넘어도 색 배열 인덱스로 사용하지 않는다. 색은 플레이어 체크포인트에 저장하므로 재접속·디스크 복구·전장 승계에서 유지한다. 색이 없는 기존 저장은 기존 색을 먼저 예약한 뒤 플레이어 ID 순서로 남은 색을 배정한다. 상시 월드 새 입장도 유령이 쓰는 색을 포함해 빈 색을 배정한다.
- 접속 순위는 합산 점수 내림차순, 동점은 플레이어 ID 오름차순이다. 접속 중인 플레이어의 닉네임·ID·합산 점수를 전역 공개한다. 유령과 패배자는 목록에서 빠진다. 위치와 합산 점수의 세부 항목은 상대에게 전송하지 않는다.
- 매20Hz 틱의 사건을 초기화 전에 수집하고10Hz로 묶는다. 같은 틱의 Death→Converted는 Converted 하나로 정리한다. Drop의 targetId는 죽은 유닛, entityId/Core는 생성된 핵이다. Heal/ShieldPulse의 amount는 실제 적용량이다.
- 연쇄·관통·범위·지속 피해는 Impact로 확정한다. 같은 틱의 추가 피해는 피격자별 합으로 표현하고 원인이 다른 경우 공격자·원소·시작점은 비운다. 개별 주 공격의 Attack은 별도로 유지한다. 흡혈도 실제 증가한 체력만 Healed로 보낸다.
- 전송 때 공간 사건이 **현재** 카메라 또는 원형 시야에 있는지 다시 검사한다. 숨겨진 공격자의 ID·소유자·원소·시작점을 제거한다(sourceId0/owner-1/element-1/hasSource0/sourcePosition0). 허용된 피격 위치만 남길 수 있다. 지형 탐색 기록은 사건의 공개 여부에 영향을 주지 않는다.
- 신뢰 프레임은 모든 길이·enum·ID 오름차순·위치·인구 합·시간·serial을 검증한 뒤 대기한다. 미래 fog revision이 준비될 때까지 **모든 결과 프레임**을 보존한다. 과거 revision의 완료 프레임은 자기 상태·ID 결과만 소비하며 `SpatialReady=false`로 이전 공간/VFX/음향 재생을 막는다.
- 미완성 신뢰 프레임을 다음 프레임으로 조용히 덮어쓰지 않는다. 연결당 미확인 전송/수신 대기는 **2초 또는1MiB**를 초과하면 연결을 종료한다. 서버는 LiteNetLib DeliveryEvent로 ACK 완료량을 추적한다. 결과를 버리고 연결을 유지하지 않는다.
- 매10Hz 프레임의 자기 수치는 도메인의 누적값이다. 신규 아군 전향 비율은 converted/(trained+converted), 초기3기 제외, 분모0은 N/A다.
- `rallyingCount`는 집결 명령을 받고 아직 합류하지 않은 수비대 수다. 본진 대기 수는 `garrison - rallyingCount`다. 집결 수가 0보다 작거나 전체 수비대 수보다 크면 본문을 거절한다.
- `garrisonArrivals`는 집결 명령으로 자기 수비대가 군단에 실제 합류한 누적 수다. 전향과 전사로 달라진 인구를 합류로 계산하지 않는다.

## 실패와 실행

JoinFailure는 None0/Malformed1/VersionMismatch2/DataMismatch3/InvalidElement4/WorldFull5/InvalidNickname6/InvalidResumeKey7/AlreadyConnected8/RoomStarted9/StorageUnavailable10이다. LobbyError는 NotHost1/NotReady2/SpawnFailed3/InvalidState4/InvalidPacket5/RoomFull6/ColorTaken7이다. CommandAdmission은 Accepted0/UnknownPlayer1/InvalidInput2/StaleSequence3/RateLimited4/QueueFull5다. 형식 오류의 거절 sequence는 0일 수 있다. CommandFailure는 None0/InsufficientCores1/PopulationCap2/Busy3/NotUnlocked4/InvalidPlacement5/BuildingLimit6/MaxLevel7/TrainingNotWaiting8이다.

서버와 클라이언트를 함께 갱신하는 절차는 [README](../README.md)를 따른다. 이전 M3 측정값은 [M3 검증](Reports/2026-11-03-m3-validation.md)에 있다.
