# 서버 Logic / Tick 아키텍처

게임 서버(`Server/THGameServer/`)의 tick 루프와 패킷 처리 구조를 설명한다.
새 세션에서 서버 로직을 다룰 때 이 문서를 먼저 읽으면 전체 흐름을 빠르게 파악할 수 있다.

관련 코드: `Server/THGameServer/OutGame/`(로그인/세션), `Server/THGameServer/InGame/`(룸/필드), `Server/THGameServer/Game/`(Player·Character 엔티티), `Server/THGameServer/Common/`(패킷 핸들러 테이블·대역 판정)

---

> 서버에는 **독립된 두 tick 서비스**가 있다. 이 1~5장은 **OutGame**(로그인/세션/로비, 300ms,
> Player 단위 병렬)을 설명한다. **InGame**(룸/필드, 100ms, 룸 단위 병렬)은 같은 구조(더블버퍼 큐 + phase)이며
> **6장**에서 차이점 중심으로 다룬다.

## 1. 전체 흐름 (한 tick) — OutGame

`OutGameService`(`OutGame/OutGameService.cs`)가 `LogicMain` 단일 스레드에서 고정 주기 tick 루프를 돈다.
tick 간격은 `OutGameService.TickIntervalMs = 300` (0.3초).

한 tick 의 처리 순서는 다음 4단계다 (`MainLoop`):

```
Event  → Prepare → Work → Arrange
```

```
long tickMs = TimeManager.Instance.TickMillis();   // monotonic (시스템 시계 변경 무관)

_eventor.Event(tickMs);                       // 1) 주기 작업

var raw      = _packetQueue.Swap();           //    IO 스레드 등이 쌓아둔 패킷을 한 번에 swap
var grouped  = GroupBySession(raw);           //    sessionId → List<PacketMessage> 로 그룹핑 (끊긴 세션 패킷은 버림)

_eventor.Prepare(grouped);                    // 2) Prepare phase
_eventor.Work(tickMs, grouped);               // 3) Work phase (worker, 병렬)
_eventor.Arrange(grouped);                    // 4) Arrange phase
```

- **Event**: 시간 기반 주기 작업(서버 정보 동기화, alive heartbeat, **만료 로그인 세션 정리** 등). 패킷과 무관.
- **Prepare**: 세션/로그인 도메인 선처리. 예) `COLoginReq` 로 **`LoginSession`**(Player 아님)을
  `PlayerArchive` 에 등록, `DOLoginAck`(DB 인증 성공) 로 `LoginSession` → `Player` 교체,
  `NetDisconnect` 로 `LoginSession` 제거(Player 는 제거를 미룬다, 2.6절).
- **Work**: **세션 워커 단위 병렬 처리(worker phase)** — `Player`(로그인 완료) + `LoginSession`(인증
  대기)을 함께 분산. 아래 3장 참조.
- **Arrange**: 후처리. 모든 워커의 Work 가 끝난 뒤 단일 tick 스레드에서 실행.
  예) `DOExitGameSessionAck` 로 Player 제거(2.6절), `NetAliveReq` 응답.

패킷 큐는 더블 버퍼(`PacketQueue`, `OutGame/PacketQueue.cs`)이고 `OutGameService` 가 소유한다.
IO 스레드와 Data 계층은 `OutGameService.EnqueuePacket` 으로 write 버퍼에 넣고, tick 스레드는 매 tick
`Swap()` 으로 O(1) 교환한 뒤 read 버퍼를 처리한다. List 를 재사용해 GC 부담이 없다.

`GroupBySession` 은 이미 끊긴 세션의 패킷을 버린다. 단 `NetDisconnect` 와 `DOExitGameSessionAck` 는
예외로 통과시킨다. 둘 다 세션이 닫힌 뒤에 도착하는 패킷이라, 버리면 Player 가 archive 에서 영영
지워지지 않는다(2.6절).

---

## 2. Eventor 구조와 phase

`LogicEventor`(`OutGame/LogicEventor.cs`)는 추상 베이스로, subclass(`OutGameLogicEventor`)가
패킷 핸들러를 등록하고 phase hook 을 구현한다.

phase 플래그(`THServerCommon/DefineEnum.cs`):

```
[Flags] enum ELogicEvent : byte { None=0, Prepare=1<<0, Arrange=1<<1, Work=1<<2 }
```

`LogicEventor` 의 핸들러는 **sessionId 단위**다 — 시그니처 `Action<long sessionId, T msg, byte flag>`.
`RegisterHandler<T>(packetId, handler, phases)` 로 등록하며, `phases` 에 명시한 phase 에서만 dispatch 된다.
같은 패킷을 Prepare/Arrange 양쪽에 등록하면 `flag` 로 phase 를 구분해 처리할 수 있다. 지금은 그렇게 쓰는 패킷이 없다.

`OutGameLogicEventor`(`OutGame/OutGameLogicEventor.cs`)에 등록된 OutGame 핸들러:

| 패킷 | phase | 처리 |
|------|-------|------|
| `NetDisconnect` | Prepare | `LoginSession`·미등록 세션은 archive 에서 즉시 제거. `Player` 는 제거를 미룬다(2.6절) |
| `DOExitGameSessionAck` | Arrange | 종료 저장 완료 — archive 에서 `Player` 제거 + InGame 에 `OIExitGameSessionReq` (2.6절) |
| `NetAliveReq` | Arrange | `NetAliveAck` 응답 |
| `COLoginReq` | Prepare | **`LoginSession`** 생성 + archive 등록 (Player 아님, 중복 차단) |
| `DOLoginAck` | Prepare | DB 인증 성공 — `LoginSession` 제거 후 `Player` 생성·등록 + `OCLoginAck` |

> 워커 단위 게임 패킷(`COGetPlayerReq` 등)은 이 테이블이 아니라 **각 워커의 핸들러 테이블**에서
> 처리한다(아래 3장). 즉 dispatch 테이블이 여럿이다:
> - sessionId 단위 / Prepare·Arrange phase → `LogicEventor._handlers` (OutGame 도메인)
> - `Player` 단위 / Work phase → `Player.Table`(`PacketHandlerTable<Player>`, 게임 도메인)
> - `LoginSession` 단위 / Work phase → `LoginSession.Table` (인증 대기 — `COLoginReq`→`ODLoginReq` 송신)
> - Data 계층 / DBWorker 스레드 → `DBService._handlers` (OD\* 요청 처리, 아래 2.5절)
>
> 한 패킷이 여러 테이블을 거칠 수 있다. 예) `NetDisconnect` 는 Eventor(Prepare)와 `Player`(Work)가,
> `DOExitGameSessionAck` 는 `Player`(Work, 로그)와 Eventor(Arrange)가 각각 처리한다.

### 2.5. 로그인 핸드셰이크 (DB 인증 왕복)

로그인은 **DB 인증 왕복**을 거친다. `Player` 는 인증 성공 후에만 생성되며, 그 전까지는 임시
`LoginSession` 이 같은 archive 슬롯을 차지한다. 단계:

1. **(tick N, Prepare)** `COLoginReq` 도착 → Eventor 가 `LoginSession` 생성·archive 등록(인증 대기/
   로그인 완료 세션의 중복 `COLoginReq` 는 차단).
2. **(tick N, Work)** `LoginSession.Execute` 가 `COLoginReq` 를 자기 핸들러로 dispatch → `ODLoginReq`
   를 `DBService.Send` 로 Data 계층에 송신(`_requested` 가드로 최초 1회만).
3. **(Data 스레드)** `DBService` 가 `sessionID % WorkerCount` 로 샤드를 골라 해당 `DBWorker`(전용 스레드,
   FIFO mailbox)에 넣는다 → worker 가 `ODLoginReq` 처리 후 `DOLoginAck` 를
   `OutGameService.EnqueuePacket` 으로 **PacketQueue 에 되돌린다**(IO 가 아니라 로직 입력 큐로 복귀).
4. **(tick N+k, Prepare)** `DOLoginAck` 가 일반 패킷 흐름으로 수신 → Eventor 가 `LoginSession` 제거 후
   `Player` 생성·등록하고 클라이언트에 `OCLoginAck` 송신.

부가 규칙:

- **타임아웃**: `DOLoginAck` 가 `LoginTimeoutMs`(10s) 내 도착하지 않으면 Event phase 의
  `RemoveExpiredLogins` 가 `LoginSession` 을 정리하고 네트워크 세션도 종료한다.
- **순서 보장**: 같은 `sessionID` 는 항상 같은 `DBWorker` 로 라우팅 → 단일 스레드 FIFO 처리이므로
  유저별 요청 순서가 보장된다(모듈러 샤딩).
- **응답을 PacketQueue 로 되돌리는 이유**: DB 응답을 tick 스레드(Prepare)의 기존 dispatch 흐름으로
  복귀시켜, archive 변경(Player 생성)이 항상 단일 tick 스레드에서만 일어나도록 보장하기 위함.

Data 계층 파일: `Data/DBService.cs`(`Singleton`, 샤딩 라우팅 + OD 핸들러 테이블 + 스텁 `DOLoginAck`·`DOExitGameSessionAck` 응답),
`Data/DBWorker.cs`(단일 샤드 전용 스레드 + `BlockingCollection` FIFO mailbox). 현재 실제 DB 연동 전 스텁.

### 2.6. 세션 종료 (DB 저장 왕복)

연결이 끊겨도 `Player` 를 바로 지우지 않는다. DB 에 종료 정보를 저장한 뒤에 지운다.
종료 저장 요청(`ODExitGameSessionReq`)은 `Player` 가 가진 AccountID·PID 로 만들기 때문이다.
(DB 연동 후 저장할 내용: 플레이 시간, 마지막 위치, 세션 로그. 현재는 스텁이 바로 ack 를 돌려준다.)

1. **(IO 스레드)** `Session.OnDisconnected` → `GameServerApp` 이 서버가 만든 `NetDisconnect` 패킷을
   OutGame 큐에 넣는다.
2. **(tick N, Prepare)** Eventor `OnNetDisconnect` — `LoginSession`·미등록 세션은 저장할 게 없으니 즉시
   제거한다. `Player` 는 그대로 둔다.
3. **(tick N, Work)** `Player.OnNetDisconnect` — `State = Disconnecting` 으로 바꾸고 `ODExitGameSessionReq` 를
   Data 계층으로 보낸다. 이미 `Disconnecting` 이면 다시 보내지 않는다.
4. **(Data 스레드)** 저장 후 `DOExitGameSessionAck` 를 OutGame 큐로 돌려보낸다.
5. **(tick N+k)** Work 에서 `Player` 가 ack 로그를 남기고, Arrange 에서 Eventor 가 archive 에서 `Player` 를
   제거한 뒤 InGame 에 `OIExitGameSessionReq` 를 보낸다. InGame 은 이걸로 `Character` 를 제거한다(6.3절).

부가 규칙:

- **InGame 정리는 이 경로 하나로만 한다.** `GameServerApp` 은 disconnect 때 InGame 에 따로 패킷을 넣지 않는다.
  저장이 끝난 뒤에 캐릭터를 지우는 순서가 보장된다.
- **세션이 닫힌 뒤 도착하는 패킷 통과**: 1장의 `GroupBySession` 이 `NetDisconnect`·`DOExitGameSessionAck` 를
  예외로 통과시키는 이유가 이 흐름이다.

---

## 3. Work phase — 세션 워커 단위 병렬 처리

`OutGameLogicEventor.Work(tickMs, grouped)` 는 자신이 소유한 `PlayerArchive` 를 `PlayerWorkExecutor.Run`
에 넘겨 위임한다. `PlayerWorkExecutor`(`OutGame/PlayerWorkExecutor.cs`)는 **상태를 갖지 않는 순수 병렬
실행기**다 — 순회 대상 컬렉션은 호출자(Eventor)가 소유하고 `Run` 인자(`IReadOnlyCollection<ISessionWorker>`)
로 전달한다. 도메인 핸들러(무엇을 처리하느냐)와 변경 축이 다른 인프라 관심사(접속 워커들을 매 tick
어떻게 분산하느냐)를 분리한 것.

```
// OutGameLogicEventor (PlayerArchive _archive 를 소유)
public override void Work(long tickMs, Dictionary<long, List<PacketMessage>> sessionPackets)
    => _workExecutor.Run(tickMs, _archive.Values, sessionPackets);

// PlayerWorkExecutor (stateless)
public void Run(long tickMs, IReadOnlyCollection<ISessionWorker> workers,
    Dictionary<long, List<PacketMessage>> sessionPackets)
{
    if (workers.Count == 0) return;
    Parallel.ForEach(workers, worker =>
    {
        long sid = worker switch { Player p => p.SessionID, LoginSession ls => ls.SessionID, _ => -1 };
        var packets = sid >= 0 && sessionPackets.TryGetValue(sid, out var list) ? list : EmptyPackets;
        worker.Execute(tickMs, packets);
    });
}
```

`PlayerArchive`(`OutGame/PlayerArchive.cs`)는 **`OutGameLogicEventor` 가 소유**하며, `ISessionWorker`
(로그인 완료 `Player` + 인증 대기 `LoginSession`)를 한 컬렉션에 함께 보관한다. 등록/제거/조회
(`TryRegister`/`Remove`/`Find`)는 Eventor 의 Prepare/Event 핸들러(로그인/disconnect/타임아웃)가 직접
호출해 워커 lifecycle 을 관리한다.

핵심 규칙:

- **순회 대상은 `_archive` 의 전체 워커**(`Player` + `LoginSession`, `grouped` 가 아님). tick 단위 로직은
  패킷이 없어도 돌아야 하므로, 패킷이 없는 워커도 매 tick `Execute` 된다. 패킷은 `SessionID` 로 매칭하고,
  없으면 공유 빈 리스트(`EmptyPackets`)를 넘긴다.
- **`Parallel.ForEach` = "한 워커 스레드가 한 세션 워커를 끝까지 담당, 끝나면 다음"** 의 동적 파티셔닝.
  한 워커의 패킷은 한 스레드가 순차 처리하므로 그 워커 상태에 race 가 없다.
- **barrier**: `Parallel.ForEach` 는 동기 블로킹이라, 모든 워커의 `Execute` 가 끝나기 전에는 반환하지
  않는다. 따라서 다음 단계인 `Arrange` 는 전 워커 처리 완료 후에야 호출된다.
- **disconnect 처리**: Prepare 에서 제거된 `LoginSession` 은 archive 에 없으므로 Work 순회에서 빠진다.
  `Player` 는 종료 저장이 끝날 때까지 archive 에 남아 Work 에서 `NetDisconnect` 를 처리한다(2.6절).

### Player.Execute (`Game/Player.cs`)

`Player` 가 패킷 핸들러 테이블과 처리 본문을 모두 소유한다(per-entity tick update 패턴).

- **핸들러 테이블은 static 공유** (`private static readonly PacketHandlerTable<Player> Table`).
  static 생성자에서 `Table.Register<T>(packetId, (p, pkt, m) => p.OnXxx(m))` 로 한 번 등록하고 모든 Player 가
  공유한다. `PacketHandlerTable<TOwner>`(`Common/PacketHandlerTable.cs`)는 `MessageParser<T>` 로 ParseFrom 을
  수행하는 dispatch 델리게이트를 만들어 보관한다. 파싱에 실패하면 Warning 로그 후 무시한다.
  - 핸들러 인자: `p`(Player), `pkt`(`PacketMessage` — SessionID 등), `m`(파싱된 메시지). body 가 없는 패킷도
    같은 `Register<T>` 로 등록한다.
- **`Execute(long tickMs, List<PacketMessage> packets)`** — worker phase 진입점:
  1. `packets` 를 **도착 순서대로** 순회하며 `Table.Dispatch` (순서 보존). 미등록 패킷은 Debug 로그 후 건너뛰고,
     핸들러 예외는 패킷마다 try/catch 로 격리한다.
  2. tick 단위 Player 로직(버프 만료 / 타이머 등) 수행 — 현재 골격(TODO).
- **응답 송신**은 `Player.Send(packetId, msg)` 헬퍼 — 자기 세션으로만 송신.
- **상태(`EPlayerState`)**: `LoggedIn` → `Entering`(입장 요청 후) → `InField`(입장 확정). 연결이 끊기면 `Disconnecting`.

게임 패킷 핸들러 추가 방법:
1. `enum.proto` 에 패킷 ID, `protocol.proto` 에 메시지를 추가하고 `compile.bat` 으로 재생성
2. `Player` static 생성자에 `Table.Register<새패킷>((int)EMessageID.새패킷, (p, pkt, m) => p.On새패킷(m));` 추가
3. `Player` 인스턴스 메서드 `private void On새패킷(새패킷 msg) { ... }` 구현
4. 응답이 필요하면 `Send((int)EMessageID.응답, ack);`

클라가 보내는 패킷이면 ID 를 CO 대역(10000~19999) 안에 둬야 OutGame 까지 들어온다(6.1절).

`LoginSession`(`OutGame/LoginSession.cs`)도 같은 패턴(`PacketHandlerTable<LoginSession>` + `Execute`)을 따른다 —
인증 대기 단계에서 `COLoginReq` 만 처리해 `ODLoginReq` 를 Data 계층으로 송신한다(위 2.5절).

---

## 4. 동시성 규약 (반드시 지킬 것)

worker phase 핸들러(`Player.On*` 및 tick 로직)는 **worker 스레드**에서 병렬 실행된다:

- **자기 자신(this Player)과 자기 세션(`Send`) 외의 전역 / 타 Player 상태를 변경하지 말 것.**
  교차 변경(타 Player, 전역 자료구조)이 필요하면 단일 tick 스레드인 **Arrange phase 로 미룬다.**
- 읽기 전용 공유 데이터(설정, static 핸들러 테이블 등) 접근은 안전.

이게 성립하는 근거:

- `PlayerArchive`(`OutGame/PlayerArchive.cs`)는 lock 없는 `Dictionary` 지만, 등록/제거는 Prepare/Event/Arrange
  (단일 tick 스레드)에서만 일어난다. Work 동안에는 `Values` 열거/읽기만 하고 archive 를 변경하지 않으므로
  안전하다.
- `Player.Table` / `LoginSession.Table` 은 static 생성자에서만 채우고 이후 읽기 전용 → 병렬 읽기 안전.
- 다른 서비스(Data, InGame)에 일을 시킬 때는 그 서비스의 큐에 넣기만 한다(`DBService.Send`,
  `InGameService.EnqueuePacket`). 둘 다 스레드 안전하다.
- `NetworkManager._sessions` 는 `ConcurrentDictionary`; `Session.Send` 는 Interlocked /
  ConcurrentQueue / CAS 로 스레드 안전. 게다가 한 세션은 한 워커만 담당하므로 동일 세션 동시 Send 도
  발생하지 않는다.
- 네트워크 계층 불변식은 `CLAUDE.md` §5.6 참조. worker 는 logic 스레드 풀에서 동작하며 IO 스레드를
  블로킹하지 않는다.

---

## 5. 파일 맵 — OutGame

| 파일 | 역할 |
|------|------|
| `GameServerApp.cs` | 서비스 시작/종료, 수신 패킷 라우팅(`PacketBand` 허용 목록, 6.1절), disconnect → `NetDisconnect` 주입 |
| `Common/PacketHandlerTable.cs` | `Player`·`LoginSession`·`GameRoom` 공통 패킷 dispatch 테이블(static, 읽기 전용) |
| `Common/PacketBand.cs` | 패킷 ID 대역 판정. 클라가 보낼 수 있는 패킷인지 확인(6.1절) |
| `OutGame/OutGameService.cs` | tick 메인 루프(`MainLoop`), phase 호출, 패킷 그룹핑(끊긴 세션 패킷 버림) |
| `OutGame/LogicEventor.cs` | Eventor 추상 베이스. sessionId 단위 핸들러 등록 + Prepare/Work/Arrange hook |
| `OutGame/OutGameLogicEventor.cs` | OutGame 도메인 Eventor. OutGame 핸들러 + 주기 `Event` + **`PlayerArchive` 소유/lifecycle**. `Work` 는 executor 로 위임 |
| `OutGame/PlayerWorkExecutor.cs` | worker phase **stateless 병렬 실행기**(`Run`). 순회 대상(archive)은 인자로 받음 |
| `OutGame/ISessionWorker.cs` | Work phase 에서 `Execute` 를 받는 세션 워커 공통 인터페이스(`Player` + `LoginSession`) |
| `OutGame/LoginSession.cs` | DB 인증 대기 임시 세션. `COLoginReq`→`ODLoginReq` 송신, 성공 시 `Player` 로 교체 (2.5절) |
| `OutGame/PacketQueue.cs` | IO↔tick 더블 버퍼 패킷 큐. **InGame 도 동일 타입 재사용** |
| `OutGame/PacketMessage.cs` | 패킷 struct (sessionId, packetId, payload). **InGame 도 공용** |
| `OutGame/PlayerArchive.cs` | 세션 워커 보관자(`Player` + `LoginSession`). `Values` 로 전체 순회. **OutGameLogicEventor 가 소유** |
| `Data/DBService.cs` | `Singleton`. OD\* 요청을 샤딩 라우팅(`sessionID % N`) + OD 핸들러 테이블. 응답은 PacketQueue 로 복귀 (2.5절) |
| `Data/DBWorker.cs` | DBService 단일 샤드 worker. 전용 스레드 + `BlockingCollection` FIFO mailbox (유저별 순서 보장) |
| `Game/Player.cs` | Player 엔티티 + static 핸들러 테이블 + `Execute`(per-entity tick) |
| `THServerCommon/DefineEnum.cs` | `ELogicEvent`(phase 플래그) / `EPlayerState`(Player 라이프사이클 상태) enum |

---

## 6. InGame 룸 시스템 (룸 단위 병렬)

"맵 = 룸"(메이플/로아식, 포탈로 끊긴 격리 공간) 구조의 필드 시뮬레이션 계층. OutGame 과 같은 구조이되,
병렬 단위가 `Player` 가 아니라 **`GameRoom`** 이다. `InGameService`(`InGame/InGameService.cs`,
`Singleton`)가 OutGameService 와 **독립된 자체 100ms tick 스레드**(`InGameMain`)를 돈다.

### 6.1. 패킷 분배 (대역 라우팅)

`GameServerApp` 이 등록한 `Session.OnPacketReceived` 가 패킷 ID 대역으로 받을 곳을 정한다.
**클라가 보낼 수 있는 대역만 받는 허용 목록 방식**이고, 판정은 `Common/PacketBand.cs` 에 모여 있다.

| 대역 (`enum.proto`) | 방향 | 네트워크에서 받으면 |
|------|------|------|
| 101~ NET | 공통 | `NetAliveReq` 만 OutGame 으로. `NetDisconnect` 는 서버가 만드는 패킷이라 거부 |
| 10000~19999 CO/OC | 클라 ↔ OutGame | OutGame 으로 (`PacketBand.IsClientToOutGame`) |
| 20100~49999 OD/DO | OutGame ↔ Data | 거부 (서버 내부) |
| 50000~59999 OI/IO | OutGame ↔ InGame | 거부 (서버 내부) |
| 60000~69999 IC | InGame → 클라 | 거부 (서버 → 클라 방향) |
| 70000~79999 CI | 클라 → InGame | InGame 으로 (`PacketBand.IsClientToInGame`) |

- 각 대역의 `*_BEGIN`/`*_END` 값은 경계 표시용이라 받지 않는다.
- CO 대역에는 OC(서버 → 클라) 패킷도 섞여 있다. 클라가 OC 패킷을 보내면 받긴 하지만 처리할 핸들러가 없어 버려진다.
- **거부한 패킷은 Warning 로그만 남기고 버린다. 세션은 끊지 않는다.** 핸들러 안에서 세션을 닫으면 같은 수신
  버퍼에 남은 다음 패킷이 계속 처리되기 때문이다(`Session.ProcessReceivedData` 루프가 닫힘 여부를 보지 않음).
- 서버 내부 패킷(OD/DO, OI/IO)은 네트워크를 거치지 않고 각 서비스의 `EnqueuePacket`/`Send` 로 직접 넣는다.
  허용 목록이 없으면 클라가 이 패킷을 위조해 로그인·상태 검사를 건너뛸 수 있다.
- 새 클라 대면 대역이 생기면 `PacketBand` 에 판정 함수를 추가하고 `GameServerApp` 에 분기를 추가한다.

InGame 도 IO 스레드는 더블버퍼 `PacketQueue` 에 넣기만 하고, tick 스레드가 매 tick `Swap` 한다.

### 6.2. tick 루프 (OutGame 과 의도적으로 다름)

두 서비스 모두 시간 소스는 `TimeManager.TickMillis()`(monotonic, `Environment.TickCount64` 기반 —
시스템 시계 점프/역행에 영향받지 않음)다. 둘 다 매 주기(~300ms / ~100ms)에 `ProcessTick` 을 1회
돌리되, InGame 은 **지난 tick 이후 실제 경과 시간(가변 dt)**을 `ProcessTick` 에 넘긴다(OutGame 은 dt 미사용):

- **`TimeManager.TickMillis()`(monotonic)** — 시스템 시계 변경 무관.
- **가변 dt** — 매 tick `dt = now − 직전 tick 시각`. 한 tick 이 밀리면 **다음 한 tick 의 dt 가 그만큼
  커질 뿐, catch-up 스텝을 따로 돌지 않는다**(`lastTickMs` 를 now 로 리셋 → burst/death spiral 없음).
  catch-up 상한은 현재 없다(필요해지면 재도입). 시뮬 로직은 dt 비례(rate × dt)로 작성한다.
- **sleep+spin 하이브리드** — 다음 100ms 경계 전까진 `Sleep`(양보), 경계 직전엔 `SpinWait`.
  경계 정밀도는 `TickMillis()` 분해능(Windows ~15.6ms)에 묶인다.

### 6.3. phase 구조 (Prepare → Work)

```
ProcessTick(dtMs):
  Prepare()        // 단일 tick 스레드. _packetQueue.Swap() 후 패킷마다:
    - OI 제어 패킷(OIEnterReq/OILeaveReq/OIExitGameSessionReq)
        → InGameService 핸들러가 SessionRoomMap·RoomRepository 를 바꾸고 해당 룸 Inbox 에 넣음
    - 그 외(CI 게임플레이 패킷)
        → SessionRoomMap 으로 룸을 찾아 Inbox 에 넣음. 어느 룸에도 없는 세션이면 버림
  Work(dtMs)       // 병렬: Parallel.ForEach(rooms, r => r.Tick(dtMs))
```

- **`SessionRoomMap`(SessionID→RoomID)·`RoomRepository` 변경은 Prepare 에서만 한다.** Work 는 읽기만 한다.
  OutGame `PlayerArchive` 와 같은 방식이다. 변경과 조회가 같은 시간에 겹치지 않으므로 lock 없이 안전하다.
- **OutGame ↔ InGame 은 패킷으로만 통신한다.** 서로 객체를 직접 참조하지 않고 상대 서비스의
  `EnqueuePacket` 으로 OI/IO 패킷을 넣는다.

**필드 입장 흐름**

1. **(OutGame Work)** `Player.OnCOEnterReq` — `State` 가 `LoggedIn` 일 때만 받는다. `Entering` 으로 바꾸고
   `OIEnterReq{RoomID}` 를 InGame 큐에 넣는다. 지금은 StageID 를 그대로 RoomID 로 쓴다(1:1).
2. **(InGame Prepare)** `InGameService.OnEnter` — 룸이 없으면 만들고, `SessionRoomMap` 에 등록한 뒤 룸 Inbox 에 넣는다.
3. **(InGame Work)** `GameRoom.OnEnter` — `Character` 를 만들고 클라에 `ICEnterNoti`, OutGame 에 `IOEnterAck` 를 보낸다.
4. **(OutGame Work)** `Player.OnIOEnterAck` — `Entering` → `InField` 로 확정한다.

**퇴장**: 세션 종료 때 OutGame Arrange 가 보내는 `OIExitGameSessionReq` 로 처리한다(2.6절). Prepare 가
`SessionRoomMap` 에서 지우고, 룸이 `Character` 를 제거한다. `OILeaveReq` 핸들러도 있지만 지금 보내는 곳은 없다.

### 6.4. GameRoom — 룸 1틱

```
GameRoom.Tick(dtMs):
  DrainInbox()     // Inbox 를 시작 시점 Count 만큼만 꺼내 처리 (소비자 1개)
  Simulate(dtMs)   // 룸 시뮬 1스텝 (현재 비어 있음)
```

- **룸 입력은 `PacketMessage` 하나로 통일했다.** 입장·퇴장·게임플레이 패킷이 모두 같은
  `Inbox`(`ConcurrentQueue<PacketMessage>`)로 들어오고, `GameRoom` 의 static `PacketHandlerTable<GameRoom>` 이
  dispatch 한다. 룸 안에서 다음 tick 으로 미룰 작업(스폰, 타이머 등)도 같은 Inbox 에 넣으면 된다.
- **drain 은 시작 시점 `Count` 만큼만 한다.** 처리 중에 룸이 스스로 넣은 패킷은 다음 tick 으로 넘어간다.
  같은 tick 안에서 끝없이 drain 하는 것을 막기 위해서다. `Count` 를 믿을 수 있는 이유: Prepare 의 enqueue 는
  Work 시작 전에 끝나고, Work 중에는 그 룸을 맡은 워커 하나만 넣는다.
- **룸 내부 상태(Character 컬렉션, Position)는 그 룸을 맡은 워커 하나만 바꾼다(single-writer, lock 없음).**
  밖에서 바꾸려면 Inbox 로 패킷을 보낸다.
- 이동(`GameRoom.MoveCharacter`)은 함수만 있고 연결된 패킷은 아직 없다. CI 대역에 `CIMoveReq` 를 추가해
  `Table.Register` 로 연결한다(로드맵 세션 11~12).

### 6.5. 브로드캐스트 (확장 지점)

- **`GameRoom.Broadcast(packetID, payload)`** — 룸 전원에게 직접 송신. 맵당 인원 캡이 있어
  룸 내부 전원 전송으로 충분하다는 도메인 전제. `_characters` 를 직접 순회하므로 별도 추상화 없음.
- **확장 지점**: 시야 기반 AOI(`GridAoi` 등)가 필요해지면 이 `Broadcast` 한 곳에서 수신자 선별로
  분기한다(그때 송신 주체/기준점 인자를 추가). 전 단계의 `IInterestManagement` 교체 경계는
  단일 전략만 쓰는 현 단계에서 불필요한 간접층이라 제거했다 — 필요 시점에 다시 도입한다.

### 6.6. Player ↔ Character 분리

- **`Player`**(OutGame, 영속) — 로그인/세션 유지의 메인 객체. 필드 진입 후에도 계속 존재.
- **`Character`**(`Game/Character.cs`, InGame) — 필드 룸이 소유하는 sessionId 스코프 캐릭터. 진입 시
  생성, 이탈 시 제거. 룸 Work 스레드 하나만 접근한다(lock 없음). `Position`(`Game/Position.cs`) 보유.
- 둘은 서로 참조하지 않는다. 잇는 끈은 SessionID 하나뿐이다.

### 6.7. 파일 맵 — InGame

| 파일 | 역할 |
|------|------|
| `InGame/InGameService.cs` | `Singleton`. 자체 100ms tick(TickMillis() 가변 dt, sleep+spin), Prepare(OI 제어 패킷 처리 + 룸 Inbox 로 라우팅)/Work(룸 병렬 실행) |
| `InGame/GameRoom.cs` | 룸 1틱(Inbox count 스냅샷 drain + 시뮬), static 핸들러 테이블, Character 컬렉션, 룸 전원 브로드캐스트 |
| `InGame/RoomID.cs` | `readonly record struct RoomID(long)` — 타입 안전 룸 ID |
| `InGame/SessionRoomMap.cs` | SessionID→RoomID. Prepare 에서만 변경, Work 는 읽기만 (lock 없음) |
| `InGame/RoomRepository.cs` | RoomID→GameRoom. 룸 생성/조회 |
| `Game/Character.cs` · `Game/Position.cs` | 필드 캐릭터 엔티티 + 좌표 값 타입 |
