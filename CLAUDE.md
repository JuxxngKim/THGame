# CLAUDE.md

## 0. Language Requirement (MUST READ FIRST)

**All responses MUST be provided in Korean (한글).**

This applies to all explanations, comments, commit messages, code reviews, and any text output directed to the user. Code itself (variable names, function names, etc.) follows the coding style guide.

---

## 1. Build Method and Project Structure

- **Solution file**: `Server/THServer.slnx` (신형식 slnx)
- **Build command**: `dotnet build Server/THServer.slnx`
- **Target framework**: `net10.0` (모든 서버 프로젝트 공통)
- **Build output**: `Server/bin/` (모든 프로젝트 공통 `BaseOutputPath`)

**Project structure**:
- `Server/THGameServer/` — 게임 서버 실행체 (Exe, namespace `TH.Server`)
- `Server/THServerCommon/` — 서버 공통 인프라 + Protocol 링크 (Library, namespace `TH.Common`)
- `Server/bin/config/` — 런타임 설정 (`profile.ini`, `config.{Env}.ini`)
- `Common/Tool/ProtocolGenerator/generated/` — protobuf 생성 코드 (THServerCommon이 링크 컴파일)
- `Client/` — UE5 클라이언트

**서버 Tick 아키텍처**: 독립된 두 tick 서비스가 있다 —
**OutGame**(`OutGame/`, 300ms, Event→Prepare→Work→Arrange, Player 단위 worker phase 병렬)와
**InGame**(`InGame/`, 100ms, 룸 단위 병렬 — "맵=룸" 필드 시뮬). 두 서비스는 동형이며 더블버퍼
PacketQueue·phase 모델을 공유한다. tick 루프, 패킷 핸들러 등록, 룸/Interest/스케줄러 교체 경계는
[`docs/server-logic-architecture.md`](docs/server-logic-architecture.md) 참조.
서버 로직(`Server/THGameServer/OutGame`·`InGame`·`Game`)을 다룰 때 먼저 읽을 것.

---

## 2. General Precautions

- Only modify `protocol.proto` and `sprotocol.proto` for proto file changes
- `Common/Tool/ProtocolGenerator/generated/*.g.cs` 는 생성물이므로 **직접 수정 금지**
- All new files MUST be created with **UTF-8 with BOM** encoding
- 코드 주석은 **한글**로 작성 (0항과 일관). 단 SAEA / ArrayPool / IOCP 같은 표준 용어는 영어 그대로 사용

---

## 3. 🚨 DB Access Rules (Strictly Enforced, No Exceptions)

- **DO NOT** execute any data/schema-modifying queries (INSERT/UPDATE/DELETE/MERGE/TRUNCATE/DROP/ALTER/CREATE, etc.) directly against the DB via the MCP MSSQL server
- The DB may **only** be queried with read-only SELECT statements
- If data addition/modification/deletion or schema changes are required, **do not execute the query directly**. Instead, create a separate SQL script file (`.sql`) and provide it so the user can review and execute it manually
- When writing the script, always include:
  - Target DB (3-part naming)
  - Scope of impact
  - Rollback method (preferably with `BEGIN TRAN` / `ROLLBACK` examples)
- **No exceptions**: Never execute write/modify queries directly, even for reasons like "just a quick one-time check"

---

## 4. Coding and Behavior Guidelines (LLM Guidelines)

**Core principle**: These guidelines prioritize **safety and caution over speed**. (For very trivial tasks, exercise flexible judgment as appropriate.)

### 4.1. Think Before Coding

**Do not assume. Do not hide ambiguity. State trade-offs explicitly.**

- Before implementing, clearly state your assumptions. If anything is uncertain, ask first.
- When multiple interpretations are possible, do not arbitrarily pick one — present the available options.
- If a simpler approach exists, suggest it. Push back on requirements when necessary.
- If something is unclear, **stop coding** and ask precise questions about the confusing parts.

### 4.2. Simplicity First

**Write the minimum code required to solve the problem. Avoid speculative implementations.**

- Do not implement features that were not requested.
- Do not over-abstract for one-off code.
- Do not add "flexibility" or "configurability" that was not requested.
- Do not write exception handling for scenarios that cannot occur.
- If you wrote 200 lines for something that could be done in 50, rewrite it. (Any code a senior engineer would call "over-engineered" must be simplified.)

### 4.3. Surgical Changes

**Modify only what is necessary. Clean up only the mess you made.**

When editing existing code:
- Do not arbitrarily "improve" adjacent code, comments, or formatting.
- Do not refactor code that isn't broken.
- Follow the existing project code style, even if it differs from your preference.
- If you find unrelated dead code during edits, **mention it but do not delete it**.

Handling fallout from your own changes:
- Remove orphaned imports, variables, and functions that became unused **as a result of your edits**.
- Do not remove pre-existing dead code unless explicitly requested.

**Verification test**: Every changed line must directly connect to the user's request.

### 4.4. Goal-Driven Execution

**Define success criteria. Iterate until verified.**

Convert tasks into concrete, verifiable goals:
- "Add validation" → "Write tests for invalid inputs first, then make them pass"
- "Fix the bug" → "Write a test that reproduces the bug, then make it pass"
- "Refactor X" → "Confirm all tests pass both before and after refactoring"

For multi-step tasks, present a brief plan first:

---

## 5. Server Coding Conventions

현재 서버 코드에서 일관되게 강제되고 있는 규칙. 신규 코드도 동일하게 따른다.

### 5.1. File / Namespace

- **file-scoped namespace** 사용 (`namespace X;` 형식, 블록 형식 금지)
- **`nullable enable`** 전체 활성화
- 파일 인코딩: **UTF-8 with BOM** (2항 재확인)
- 클래스 파일 = 1 public 타입 원칙

### 5.2. Singleton

- 공유 매니저는 `TH.Common.Singleton<T>` 베이스 사용
- 구현 클래스는 `sealed` + `private` 무인자 생성자
- 인스턴스 접근은 `XxxManager.Instance` (Lazy<T> 기반, 스레드 안전)
- 예시: `ConfigManager`, `TimeManager`, `NetworkManager`, `SystemTimeProvider`

### 5.3. Time / Date

- 한국 시간 조회: `TimeManager.Instance.NowKst()` → `THDateTime` (KST 의미 내장)
- 저장 / 직렬화 / 로깅용 UTC: `TimeManager.Instance.UtcNow()`
- Unix ms: `TimeManager.Instance.UnixMillis()`
- **`DateTime.Now` / `DateTime.UtcNow` 직접 호출 금지** — 테스트 가능성과 KST 일관성을 위해 항상 `TimeManager` 경유

### 5.4. Configuration

- 접근: `ConfigManager.Instance.Get(section, key)` / `GetRequired(section, key)`
- 환경 구분: `profile.ini` 의 `[Profile] Env=...` 값으로 `config.{Env}.ini` 를 로드
- 인스턴스 식별: `[Profile] Id=...`. 서버 인스턴스별 섹션은 **`Game.{Id}` 패턴**을 사용 (예: `[Game.1] BindAddr=...`)
- 부트스트랩 순서: **Time → Config → Log → Network** (변경 금지)

### 5.5. Logging

- **Serilog** 사용. `Log.Information / Debug / Warning / Error / Fatal`
- 로그 메시지는 **영어 (ASCII)**, 구조화 로깅 (`"Session {Id} closed"` 형태로 placeholder + 인자 분리)
  - 이유: 콘솔 codepage / 파일 인코딩 호환성, 로그 수집·검색 도구(ELK, Loki 등) 친화성
  - 단, 코드 주석은 §2 와 일관되게 **한글** 유지
- 출력 템플릿 / Sink 는 `LoggerSetup` 에서만 구성, 다른 곳에서 재구성 금지

### 5.6. Network Layer Invariants (`Server/THServerCommon/Network/`)

라이브 디버깅으로 확보한 불변식. **수정 시 반드시 유지한다**:

1. **`Session.OnDisconnected` 는 세션당 정확히 1회 발화**한다. CAS 게이트(`_closed`)로 보장.
2. 모든 종료 경로는 `Session.Close(notify: bool)` 한 함수로 통일 (A안). IO 에러는 `HandleDisconnect()` → `Close(notify: true)`.
3. **`NetworkManager` 는 `OnSessionDisconnected` 를 직접 발화하지 않는다.** 항상 `Session.OnDisconnected` → `OnSessionDisconnectedInternal` 경로를 통해서만 발화. `CloseSession` 도 `session.Close(notify: true)` 만 호출한다.
4. **패킷 핸들러(`OnPacketReceived`)는 IO 스레드에서 호출된다 — 블로킹 금지.** 무거운 작업은 로직 큐로 enqueue.
5. SAEA 는 **Dispose 생략**. `Socket.Close` 이후 in-flight 콜백이 `OperationAborted` 로 도착하는 race 를 회피. GC 가 회수.
6. 송신 버퍼는 `ArrayPool<byte>.Shared` 사용 — `Send` 에서 **백프레셔 체크 → Rent 순서** (역순 금지).
7. 비동기 IO 가 동기 완료될 때 **재귀 호출 금지**, while 루프로 처리 (스택 오버플로 방지).