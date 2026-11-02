# 실시간 네트워크 패턴과 기법

이 문서는 GoF 패턴 외의 네트워크 아키텍처·동기화 기법도 포함한다. 이름을 확인한 것과 프로젝트에 채택한 것을 구분한다. [기존 통신 규칙](../architecture.md)과 [패킷 정의](../../protocol.md)가 우선이다.

| 패턴·기법 | 역할과 적용 기준 |
| --- | --- |
| Authoritative Server | 서버가 상태와 판정을 소유하고 클라이언트는 명령을 보낸다. 현재 프로젝트의 필수 구조다. |
| Client-Side Prediction | 서버 응답 전에 내 군단 중심점 이동만 예측한다. 전투·경제 판정까지 예측 범위를 넓히지 않는다. |
| Server Reconciliation | 서버 확정 상태와 미확정 입력을 이용해 예측 이동을 보정한다. 입력 번호·재적용 범위를 정의한다. |
| Snapshot / Entity Interpolation | 수신한 상태 사이를 보간해 다른 유닛을 표현한다. JSON의 지연 설정을 따른다. |
| Dead Reckoning / Extrapolation | 마지막 상태로 미래 위치를 추정한다. 현재 보간 정책을 대체하려면 별도 근거와 검증이 필요하다. |
| Interest Management / AOI | 클라이언트별 관심·시야 범위로 전송 대상을 제한한다. 시야 밖 정보는 보내지 않는다. |
| Delta Snapshot / Quantization | 기준 상태 대비 차이와 제한된 정밀도로 대역폭을 줄인다. 기준 손실·범위 초과·오차를 처리한다. |
| Sequence / Acknowledgment / Deduplication | 오래되거나 중복된 입력·응답을 식별한다. LiteNetLib의 보장을 확인하고 중복 구현을 피한다. |
| Deterministic Lockstep | 입력만 공유하고 각 피어가 같은 시뮬레이션을 수행하는 비교 대상이다. 현재 시야 비공개·서버 권위 구조에 도입하지 않는다. |
| Rollback / Lag Compensation | 과거 상태 재생·판정 보정이 필요한 별도 설계에서 검토한다. 현재 전체 월드 롤백이나 전투 지연 보상을 임의로 추가하지 않는다. |

예측·보정은 [Gabriel Gambetta 원문](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html), 보간은 [Entity Interpolation](https://gabrielgambetta.com/entity-interpolation.html)을 참고한다. 스냅샷·압축·상태 동기화·Lockstep은 [Glenn Fiedler의 원문 목록](https://www.gafferongames.com/), [Snapshot Compression](https://new.gafferongames.com/post/snapshot_compression/)에서 해당 글을 확인한다.

패턴 도입 시 패킷 필드·순서·신뢰 채널·시야 필터 테스트와 대역폭 측정을 함께 갱신한다. 네트워크 패킷 유실과 서버 틱 지연은 서로 구분해 검증한다.
