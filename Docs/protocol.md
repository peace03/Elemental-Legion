# 네트워크 프로토콜 — M3 / version 3

LiteNetLib **1.3.5**, little endian 고정 필드 바이너리다. 모든 앱 패킷은 **1024B 이하**다. `u8/u16/u32/u64`, `i16/i32/i64`, `f32`를 사용한다. 좌표는 XZ 평면의 미터다. 게임 수치 원본은 JSON이며 전송 기술 상수로 인구·시야·밸런스를 바꾸지 않는다.

## 연결과 명령

- Hello에서 version **3**, LF 정규화 UTF-8 gamedata SHA-256, 허용된 시작 자연 원소를 검사한다. v1/v2는 VersionMismatch로 거절한다. 해시 일치는 계정 인증이나 암호화가 아니다.
- 서버가 peer에 playerId를 연결한다. C→S 명령에는 소유자·좌표·피해·결과가 없다. Move/Train/Rally가 하나의 증가하는 sequence와 JSON의 수신 제한·큐를 공유한다.
- Move는 유한한 방향을 안전하게 정규화한다. Train은 유효한14원소, Rally는 추가 인자 없음, 모든 sequence는0보다 커야 한다. 중복·역행 입력은 거절한다.
- 접수 결과와 실제 실행 결과는 구분한다. Accepted는 접수만 뜻한다. 양성 실행 실패는 Gameplay의 CommandRejected로 전달한다. 비용·예약 인구·전향 경쟁은 서버에서 판정한다.
- Pose.appliedSequence까지의 **이동 입력만** 예측에서 제거·재적용한다. Train/Rally를 이동 이력에 넣지 않는다. 이미 관측한 건물은 공유 선분–원 헬퍼로 중심을 예측하고 숨겨진 장애물은 서버 보정으로 처리한다.
- 종료 시 세션 복제·시야·탐색·보간·효과를 초기화한다. 새 Baseline 전에는 전체 미탐색이다. 서버 월드는 종료한 군단·본진을 보존하며 M4/M5 전에는 슬롯 재사용·계정 재접속을 제공하지 않는다. 실행당 새 세션은 JSON maxPlayers까지다.

## 메시지와 채널

채널0은 명령·응답 및 비신뢰 위치, 채널1은 신뢰 시야·표시 해제, 채널2는 신뢰 자기 상태·정적 관측·전투 결과다. 신뢰 메시지는 ReliableOrdered다. 명령 응답이 큰 시야/게임 프레임 뒤에 대기하지 않도록 큐를 분리한다.

| ID | 메시지 | 필드 순서 | 크기/전송 |
| --- | --- | --- | --- |
| 1 | Hello C→S | kind:u8, version:u16, dataHash:32 bytes, startElement:u8 |36B 연결 요청|
| 2 | Welcome S→C |kind:u8, playerId:i32|5B 채널0 신뢰|
| 3 | Move C→S |kind:u8, sequence:u64, directionX:f32, directionZ:f32|17B 채널0 신뢰|
| 4 | Pose S→C |kind:u8, tick:i64, playerId:i32, appliedSequence:u64, centerX:f32, centerZ:f32, directionX:f32, directionZ:f32|37B 채널0 Unreliable|
| 5 | Units S→C |kind:u8, tick:i64, recipient:i32, anchorX:f32, anchorZ:f32, total:u16, part:u16, visionRevision:u32, records|29B +24B×최대41, 채널0 Unreliable|
| 6 | Rejected S→C |kind:u8, sequence:u64, admission:u8|10B 채널0 신뢰|
| 7 | FogBaseline S→C |kind:u8, tick:i64, recipient:i32, revision:u32, baseRevision:u32, totalBytes:i32, offset:i32, body|29B +최대995B 채널1|
| 8 | FogDelta S→C |FogBaseline과 같은 헤더, 변경분 body|채널1|
| 9 | Forget S→C |kind:u8, tick:i64, recipient:i32, revision:u32, count:u16, (entityKind:u8,id:i32)×count|19B +5B×최대201, 채널1|
|10|Train C→S|kind:u8, sequence:u64, element:u8|10B 채널0 신뢰|
|11|Rally C→S|kind:u8, sequence:u64|9B 채널0 신뢰|
|12|Gameplay S→C|kind:u8, tick:i64, recipient:i32, visionRevision:u32, batch:u32, totalBytes:i32, offset:i32, body|29B +최대995B 채널2|
|13|Accepted S→C|kind:u8, sequence:u64|9B 채널0 신뢰|

Units record: `id:i32, owner:i32, element:u8, relativeX:i16, relativeZ:i16, flags:u8, health:f32, shield:f32, statusMask:u16`. owner=-1은 중립, flags bit0=수비대/bit1=전향됨, 나머지0. 상태 bit0..6=Wet/Chill/Burn/Radiation/Freeze/EarthMark/Smoke, bit7=Haste, bit8=Steam, bit9=Sand. 체력·방벽·상태는 표시용 서버 결과다.

- 상대 좌표는 `round((position-anchor)/mapSize*32767)`다. 축별 최대 오차는 `mapSize/65534`m이며 디코딩 반올림 경계만 보정한다.
- 기술 상한4096기, 청크당41기, 최대100청크다. 64비트 조각 마스크에 의존하지 않는다. 빈 관측도 헤더1청크다.
- 동일 tick/recipient/anchor/total/revision의 모든 조각이 검증된 뒤 원자 반영한다. 잘림·추가 바이트·잘못된 인덱스/값·중복 완성·과거 프레임은 적용하지 않는다. 조각 역순과 내용이 같은 중복은 허용한다.
- 미래 시야 revision의 Units는 최신 완성 프레임 하나만 대기한다. 과거 revision은 폐기한다. Forget 이하 틱은 복원하지 않으며 새 재관측만 허용한다. 전향 결과는 ID를 유지하며 과거 소유자·체력 복원을 막고 최종 사망 ID는 다시 표시하지 않는다.

## 시야와 종류별 표시 해제

- 지형 격자는 `ceil(mapSize/visionCellSize)`, 현재168×168/칸10m다. 시야 원과 겹치는 칸을 현재 시야로, 그 합집합을 누적 탐색으로 표시한다. 유닛·사건 인가는 JSON25m의 정확한 원 거리로 검사한다.
- 칸 번호는 x+z×width, 비트는 LSB부터다. Baseline body는 현재 packed bitmap 뒤에 누적 탐색 bitmap이다. 현재7056B/8청크/7288B이며 Welcome까지7293B다. **v3 총 초기 비용에는 별도의 Gameplay도 포함**되므로 실제 플레이어별 보고를 사용한다.
- Delta는 `count:i32, (cellIndex:i32,flags:u8)×count`, 오름차순·중복 없음. flags는0=미탐색,2=탐색,3=현재 시야,1은 금지다. 탐색은 연결 안에서 감소하지 않는다.
- 최초 revision1/base0, 변경 때만+1/base직전이다. offset은995의 배수다. 동일 revision/tick/owner/kind/total과 마지막 길이·모든 셀을 검증한 뒤 적용한다. 미완성·base 불일치로 현재 표시를 바꾸지 않는다.
- entityKind는 Unit0/Core1/Building2/Area3이다. 건물·영역은 별도 ID 공간으로 안정적인 ID를 유지한다. Forget은 이전에 관측한 종류별 ID 중 사라진 것만 포함한다. 좌표·체력·시야 밖 사건은 없다. 같은 채널에서 시야 변경분보다 먼저 보낸다.
- 핵·건물·영역도 Forget 틱 이하의 대기 프레임에서 복원하지 않는다. 지속 영역은 유효 관측 안에 있는 동안 유지하고 만료·시야 이탈·재관측을 구분한다. 건물에 없는 체력·피격·파괴 판정은 추가하지 않는다.

## Gameplay body

공간적으로 움직이지 않는 관측과 중요 결과는 채널2에서 함께 보낸다. 자기 상태에는 상대의 경제·양성이 없다. count는 i32이며 record는 아래 고정 순서다.

| 부분 | 필드/기술 상한 |
| --- | --- |
|자기 상태76B|baseX/Z:f32×2, mainElement:i32, baseLevel:i32, cores:i64, population:i32, reserved:i32, cap:i32, garrison:i32, trainingElement:i32(-1=없음), trainingStartTick:i64, trainingEndTick:i64, trained:i64, converted:i64|
|구성|공유 원소 순서별 count:i32×14; 합은 population|
|건물|count:i32, (id:i32,owner:i32,kind:u8,position:f32×2,radius:f32)×count; 최대256|
|핵|count:i32, (id:i32,amount:i32,position:f32×2,endTick:i64)×count; 최대4096|
|영역|count:i32, (id:i32,reaction:u8,position:f32×2,radius:f32,endTick:i64)×count; 최대4096|
|사건|count:i32, event69B×count; 최대8192|

event69B: `kind:u8, owner:i32, sourceId:i32, targetId:i32, position:f32×2, amount:f32, detail:i32, commandSequence:u64, element:i16, hasSourcePosition:u8, sourcePosition:f32×2, entityKind:u8, entityId:i32, eventTick:i64, serial:u64`.

- eventKind: CommandRejected0, Attack1, Reaction2, Death3, Converted4, CoreDropped5, CorePickedUp6, Trained7, Healed8, ShieldPulse9, Impact10. 건물kind는 Base0/NatureForge1/EnergyForge2, reaction은 JSON의6반응 순서다.
- 매20Hz 틱의 사건을 초기화 전에 수집하고10Hz로 묶는다. 같은 틱의 Death→Converted는 Converted 하나로 정리한다. Drop의 targetId는 죽은 유닛, entityId/Core는 생성된 핵이다. Heal/ShieldPulse의 amount는 실제 적용량이다.
- 연쇄·관통·범위·지속 피해는 Impact로 확정한다. 같은 틱의 추가 피해는 피격자별 합으로 표현하고 원인이 다른 경우 공격자·원소·시작점은 비운다. 개별 주 공격의 Attack은 별도로 유지한다. 흡혈도 실제 증가한 체력만 Healed로 보낸다.
- 전송 때 공간 사건의 **현재** 정확한 원 시야를 다시 검사한다. 숨겨진 공격자의 ID·소유자·원소·시작점을 제거한다(sourceId0/owner-1/element-1/hasSource0/sourcePosition0). 허용된 피격 위치만 남길 수 있다. 단순히 지형 격자가 탐색됐다는 이유로 사건을 보내지 않는다.
- 신뢰 프레임은 모든 길이·enum·ID 오름차순·위치·인구 합·시간·serial을 검증한 뒤 대기한다. 미래 fog revision이 준비될 때까지 **모든 결과 프레임**을 보존한다. 과거 revision의 완료 프레임은 자기 상태·ID 결과만 소비하며 `SpatialReady=false`로 이전 공간/VFX/음향 재생을 막는다.
- 미완성 신뢰 프레임을 다음 프레임으로 조용히 덮어쓰지 않는다. 연결당 미확인 전송/수신 대기는 **2초 또는1MiB**를 초과하면 연결을 종료한다. 서버는 LiteNetLib DeliveryEvent로 ACK 완료량을 추적한다. 결과를 버리고 연결을 유지하지 않는다.
- 매10Hz 프레임의 자기 수치는 도메인의 누적값이다. 신규 아군 전향 비율은 converted/(trained+converted), 초기3기 제외, 분모0은 N/A다.

## 실패와 실행

JoinFailure: None0/Malformed1/VersionMismatch2/DataMismatch3/InvalidElement4/WorldFull5. CommandAdmission: Accepted0/UnknownPlayer1/InvalidInput2/StaleSequence3/RateLimited4/QueueFull5. 형식 오류의 거절 sequence는0일 수 있다. CommandFailure: None0/InsufficientCores1/PopulationCap2/Busy3/NotUnlocked4.

최신 소스를 빌드하는 PowerShell 절대 경로와 이전 v2 서버 종료·v3 재접속은 [README](../README.md), 측정·보류 항목은 [M3 검증](Reports/2026-11-03-m3-validation.md)을 따른다. 저장·계정 복원은 M5,15피어·유실 주입 부하는 M6다.
