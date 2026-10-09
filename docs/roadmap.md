# THGame 개발 로드맵

> 작성일: 2026-09-15. 하루 1시간 작업 전제, 총 약 46세션(주 5회 기준 9~10주).
> 세션 2는 dll 버전 조합 추적 비용 때문에 2a/2b로 나눔 (1시간 초과 판단).
> 세션 완료 시 체크박스를 채우고, 맨 아래 **다음 세션** 절을 갱신한다.

## 목표

1. Unity 클라이언트 연결: 로그인 → 룸 진입 → 이동 → 이동 동기화
2. MySQL 연동 (현재 `DBService` stub 대체)
3. Docker 컨테이너화 → 로컬 Kubernetes 배포

## 작업 규칙 (하루 1시간 전제)

1. **한 세션 = 한 커밋.** 세션 끝에 빌드가 깨져 있으면 안 된다. 절반만 된 기능은 브랜치로 뺀다.
2. **완료 판정은 표의 검증 열로.** 검증이 안 되면 세션은 끝난 게 아니다.
3. **마지막 5분은 다음 세션 첫 작업을 아래 "다음 세션" 절에 한 줄로 남긴다.** 1시간 작업은 워밍업 비용이 20~30%라 이게 없으면 매번 10분을 잃는다.
4. 순서는 "화면에 보이는 것 먼저". 동기 유지가 가장 큰 리스크라 Unity 파트를 앞에, 인프라를 뒤에 둔다.

## 시작 시점 현황 (2026-09-15)

- 서버: 로그인 → 입장(`ICEnterNoti`)까지 동작. `GameRoom.MoveCharacter`·`Broadcast` 존재하나 클라 대면 이동 패킷 없음.
- DB: `Data/DBService.cs` stub. `config.local.ini`의 `[DB] Membership`은 MSSQL 형식, `CLAUDE.md` §3도 MSSQL MCP 기준 → MySQL 전환 시 둘 다 갱신.
- 인프라: Dockerfile / k8s 매니페스트 / CI 없음 (`.github/workflows/` 빈 폴더).
- Unity: `Client/THClient`, 6000.3.23f1 + URP 17.3 생성 완료, `265cc28`에 커밋됨(템플릿 `TutorialInfo/`·`Readme.asset` 포함 상태). `.gitignore` Unity 규칙 적용 완료.
- UE5 클라: `archive/ue5-client` 브랜치에 보존.

---

## M1. Unity 접속·입장 (세션 1~10)

| 완료 | # | 작업 | 검증 |
|:---:|---|---|---|
| [x] | 1 | `Assets/TutorialInfo/`·`Readme.asset` 삭제(에디터에서), Unity 프로젝트 커밋, `CLAUDE.md` Unity 절 추가 | push 후 clone에서 Unity가 열림 |
| [x] | 2a | `compile.bat`에 Unity용 `--csharp_out` 추가(→ `Assets/Scripts/Protocol/Generated/`), 생성 코드 배치, `TH.Protocol.asmdef` | Unity 콘솔에 Google.Protobuf 누락 에러만 남음 |
| [x] | 2b | Google.Protobuf(서버와 동일 3.34.1, 2.1 용이 없어 **netstandard2.0**)를 `Assets/Plugins/`에 배치, `link.xml`(IL2CPP 스트리핑 대비). `System.Memory`·`System.Buffers` 는 Unity 런타임에 있어 생략, Unsafe 는 `com.unity.pipeline` 의 것을 같이 씀(CLAUDE.md 1항) | Unity 컴파일 통과, `COLoginReq` 타입 참조 가능 |
| [x] | 3 | Protobuf 직렬화 단위 테스트 1개(Unity Test Framework). 30분 분량이라 남는 시간은 세션 4 준비 | 테스트 통과 |
| [ ] | 4 | `PacketHeader` 복제(8바이트: int32 length LE + int32 packetID LE) + `TcpConnection` 수신 스레드 → `ConcurrentQueue` | 서버 접속 후 소켓 열림 로그 |
| [ ] | 5 | 송신 경로 + `PacketDispatcher`(메인 스레드 `Update`에서 큐 분배, `Dictionary<int, Action<byte[]>>` 핸들러 테이블) | 서버 `NetDisconnect` 로그로 왕복 확인 |
| [ ] | 6 | `Close()` 단일 종료 경로, `OnDisconnected` 정확히 1회 보장(CAS), 에디터 Play 종료 시 소켓 정리 | 서버 로그에 세션 정리 1회만 기록 |
| [ ] | 7 | Title 씬: 접속 → `COLoginReq`(PID·AuthToken 더미) → `OCLoginAck` | 화면에 AccountID 표시 |
| [ ] | 8 | `COEnterReq(StageID=1)` → `ICEnterNoti` 수신 → Field 씬 로드 | 씬 전환 |
| [ ] | 9 | 캐릭터 프리팹 스폰(`Position` 반영), 원근 카메라 + 빌보드 스프라이트 | 캐릭터가 서버 좌표에 보임 |
| [ ] | 10 | 더미 클라 2개 + Unity 1개 동시 접속, 예외 경로(서버 꺼짐·재접속) 수동 테스트, 버그 수정 | 서버 종료 시 Unity가 Title로 복귀 |

**설계 메모**
- `THServerCommon`은 net10.0이라 Unity가 참조 불가. 헤더 파싱 십수 줄은 복제한다. 공용 netstandard 라이브러리 분리는 현 단계에서 과설계.
- 스레드 모델은 **수신 스레드 → 메인 스레드 dispatch** 단일 경로로 고정. Unity API 메인 스레드 제약 + 서버 single-writer 규약과 동일한 이유.
- 프로토콜 공유는 **생성물 두 벌 커밋(A안)**. junction 방식은 Windows 전용·clone 후 수동 설정 필요·서버 폴더에 `.meta` 오염이라 배제.
- 레퍼런스: `Server/THDummyClient/Program.cs`가 로그인→입장 시퀀스를 순수 TCP로 구현하고 있다.

---

## M2. 이동 및 이동 동기화 (세션 11~20)

### 설계 결정: A안(클라 위치 보고형)으로 시작, B안 seam 유지

| 방식 | 장점 | 잃는 것 |
|---|---|---|
| **A. 클라 위치 보고형** — 클라가 `CIMoveReq(Position)` 송신, 서버는 속도 검증 후 `ICMoveNoti` 룸 브로드캐스트 | 구현 빠름, 조작감 좋음 | 서버가 위치를 신뢰 → 치트 취약, 서버 시뮬을 보여주기 어려움 |
| **B. 입력 전송·서버 시뮬형** — 클라가 방향 입력 송신, 서버 100ms tick이 위치 계산 후 브로드캐스트 | 서버 authoritative, 포트폴리오 설득력 | 클라 예측·보정(reconciliation) 필수 → 세션 수 2배 |

**결정**: A안으로 시작. 서버 `MoveCharacter`에 속도 상한 검증만 넣고, 패킷 이름은 `CIMoveReq`로 두어 B안 전환 시 메시지 하나만 교체. 탄막은 시드+시작시각 동기화 방식이라 이동 방식과 독립.

| 완료 | # | 작업 | 검증 |
|:---:|---|---|---|
| [ ] | 11 | proto: `CIMoveReq`, `ICMoveNoti`, `ICLeaveNoti`, `ICRoomSnapshot`(입장 시 기존 캐릭터 목록) 추가, `compile.bat` 재생성 | 서버·Unity 컴파일 |
| [ ] | 12 | 서버: `CIMoveReq` 핸들러 → `MoveCharacter` → `Broadcast`. CI 대역 라우팅 확인(`InGameService` 패킷 분배) | 더미 클라 2개로 브로드캐스트 수신 로그 |
| [ ] | 13 | 서버: 입장 시 `ICRoomSnapshot` 송신, 퇴장 시 `ICLeaveNoti` 브로드캐스트 | 더미 클라 로그 |
| [ ] | 14 | 서버: 속도 상한 검증 + 위반 로그. 단위 테스트 | 테스트 통과 |
| [ ] | 15 | Unity: Input System WASD 이동, 로컬 캐릭터 즉시 이동 + `CIMoveReq` 송신(100ms 스로틀) | 서버 로그 |
| [ ] | 16 | Unity: 원격 캐릭터 스폰/제거(`ICRoomSnapshot`, `ICLeaveNoti` 처리) | 에디터 2개 실행 시 서로 보임 |
| [ ] | 17 | Unity: 원격 캐릭터 보간(위치 lerp, 100ms 버퍼) | 끊김 없이 이동 |
| [ ] | 18 | Unity: 네트워크 디버그 HUD(RTT, 송수신 패킷 수, 큐 길이) | 화면 표시 |
| [ ] | 19 | 서버: `NetAliveReq/Ack` 핸들러 + Unity 하트비트, RTT 측정 | HUD에 RTT |
| [ ] | 20 | 부하 테스트: 더미 클라 50개 이동 + Unity 1개. 서버 tick 시간 로그 확인, 병목 정리 | tick 100ms 이내 유지 |

---

## M3. MySQL (세션 21~30)

| 완료 | # | 작업 | 검증 |
|:---:|---|---|---|
| [ ] | 21 | `docker-compose.yml`로 MySQL 8 로컬 기동(볼륨, 초기 스키마 마운트). 로컬 MySQL 설치 없이 시작 | `mysql` 클라로 접속 |
| [ ] | 22 | 스키마 `.sql`: `account`, `character`, `session_log`. `CLAUDE.md` §3 규칙대로 스크립트 파일로 제공(대상 DB·영향 범위·롤백 포함) | 사용자가 직접 실행 |
| [ ] | 23 | `MySqlConnector` 패키지, `[DB]` 설정을 MySQL 접속 문자열로 교체, `DBWorker`에 커넥션 보유 | 기동 시 접속 로그 |
| [ ] | 24 | `ODLoginReq` 실제 처리: PID로 account 조회/생성, `DOLoginAck`에 AccountID 채움 | 재접속 시 같은 AccountID |
| [ ] | 25 | 트랜잭션·실패 경로: DB 다운 시 `DOLoginAck` 실패 코드 + 클라 에러 표시 | MySQL 컨테이너 중지 후 테스트 |
| [ ] | 26 | `ODExitGameSessionReq`: 마지막 위치·플레이타임 저장 | 재접속 시 마지막 위치에 스폰 |
| [ ] | 27 | 샤딩 라우팅 키를 AccountID 기준으로 정리, DB 워커 수 설정 반영 | 로그로 워커 분배 확인 |
| [ ] | 28 | DB 쿼리 지연 로그(느린 쿼리 임계값), 커넥션 회수 확인 | 의도적 sleep 쿼리로 경고 로그 |
| [ ] | 29 | `CLAUDE.md` §3을 MySQL 기준으로 갱신, `docs/server-logic-architecture.md` Data 절 갱신 | 문서 리뷰 |
| [ ] | 30 | 통합 테스트: 더미 클라 20개 로그인→입장→종료 반복, DB row 확인 | row 수 일치 |

---

## M4. Docker (세션 31~36)

| 완료 | # | 작업 | 검증 |
|:---:|---|---|---|
| [ ] | 31 | 서버 `Dockerfile`(multi-stage: `dotnet publish` → runtime 이미지). config는 볼륨 마운트 | 이미지 빌드 |
| [ ] | 32 | `docker-compose.yml`에 서버 + MySQL 묶기, 서버가 컨테이너 네트워크로 DB 접속 | Unity에서 접속 성공 |
| [ ] | 33 | 환경변수로 `Env`/`ID`/DB 호스트 오버라이드(`ConfigManager` seam) | compose에서 env만 바꿔 기동 |
| [ ] | 34 | 로그 stdout 출력 확인(Serilog Console sink) + 파일 로그 볼륨 | `docker logs`로 확인 |
| [ ] | 35 | Graceful shutdown: SIGTERM 수신 시 세션 정리 후 종료. 현재 서버에 있는지 먼저 확인 | `docker stop` 시 세션 정리 로그 |
| [ ] | 36 | GitHub Actions: push 시 `dotnet build` + 이미지 빌드 | CI 녹색 |

---

## M5. Kubernetes (세션 37~45)

**전제**: 로컬 k8s는 **Docker Desktop 내장 Kubernetes** 사용(이미 설치된 도구, kind/minikube보다 설정 적음).
게임 서버는 TCP 상태 유지형이라 **replica 1**로 시작. 수평 확장은 Redis 기반 세션 라우팅이 필요한 별도 마일스톤(`[UseRedis]`가 seam).

| 완료 | # | 작업 | 검증 |
|:---:|---|---|---|
| [ ] | 37 | Docker Desktop k8s 활성화, `kubectl` 확인, 네임스페이스 생성 | `kubectl get nodes` |
| [ ] | 38 | MySQL `StatefulSet` + `PVC` + `Service`, 스키마 init `Job` | pod Running, 스키마 존재 |
| [ ] | 39 | 서버 `ConfigMap`(ini) + `Secret`(DB 비밀번호) | pod 안에서 config 확인 |
| [ ] | 40 | 서버 `Deployment`(replica 1) + `Service`(NodePort 또는 LoadBalancer) | Unity가 NodePort로 접속 |
| [ ] | 41 | liveness/readiness probe: TCP 헬스체크 포트 또는 최소 HTTP 엔드포인트 추가 | probe 통과 |
| [ ] | 42 | 리소스 requests/limits, `kubectl logs` 확인, 크래시 시 재시작 확인 | 강제 kill 후 재기동 |
| [ ] | 43 | `terminationGracePeriodSeconds`와 세션 35 graceful shutdown 연동 | rollout 시 클라 정상 종료 |
| [ ] | 44 | Kustomize로 local/dev 오버레이 분리 | 두 환경 적용 |
| [ ] | 45 | `docs/deploy.md`: 로컬 기동부터 k8s 배포까지 명령 순서 | 문서대로 처음부터 재현 |

---

## 범위 밖 (이후 마일스톤 / seam)

- **수평 확장·Redis pub/sub**: replica 2 이상은 세션 라우팅·룸 배치 문제가 따라옴. `[UseRedis]` 플래그가 seam.
- **서버 authoritative 이동(B안)**: A안의 `CIMoveReq` seam 위에서 M5 이후 진행.
- **클라우드 k8s(EKS/GKE)**: 로컬 k8s에서 매니페스트 검증 후 클러스터만 교체.
- **탄막 동기화**: 패턴 시드 + 시작 시각 동기화 + 클라 결정적 시뮬. 이동 동기화와 독립.
- **Git LFS**: 대형 아트 에셋 유입 시점에 도입.

---

## 다음 세션

- [ ] **세션 4**: 클라 네트워크 코드 시작. 먼저 asmdef 를 정한다(예: `Assets/Scripts/Network/TH.Network.asmdef`, `TH.Protocol` 참조). 그다음 `PacketHeader` 복제(8바이트: int32 length LE + int32 packetID LE, 서버 `THServerCommon/Network/PacketHeader.cs` 기준)와 `TcpConnection` 수신 스레드 → `ConcurrentQueue` 를 만든다. 헤더 쓰기/읽기는 세션 3 처럼 EditMode 테스트로 먼저 고정한다. Unity 는 C# 9 라서 file-scoped namespace 를 못 쓴다.
