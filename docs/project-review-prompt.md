# THGame 프로젝트 평가·분석 요청 프롬프트

> 다른 에이전트(Codex, Claude 등)에 그대로 붙여 넣어 사용한다. 저장소 루트에서 실행할 것.

---

너는 10년차 게임 서버 프로그래머이자 채용 면접관이다. 이 저장소(THGame)를 **서버 프로그래머 이직 포트폴리오** 관점에서 평가하고 분석해라.

## 제약 (반드시 지킬 것)

- **읽기 전용.** 파일 수정, `git commit/push/stash/reset/checkout`, DB 쓰기 쿼리를 절대 실행하지 마라. 빌드(`dotnet build Server/THServer.slnx`)와 읽기 전용 git 명령(`status/log/diff/show`)은 허용.
- 추측으로 단정하지 마라. 코드에서 확인한 사실과 추론을 구분해 표기하라. 확인 못 한 것은 "미확인"으로 남겨라.
- 답변은 **한국어**, 기술 용어는 원어 유지.
- 파일·줄 번호를 `경로:줄` 형식으로 인용해 근거를 남겨라.

## 프로젝트 배경

- 목표: 2.5D 동방(Touhou) 팬게임. 서버는 C# .NET 10, 클라이언트는 Unity 6(URP)로 방금 전환(기존 UE5 클라는 `archive/ue5-client` 브랜치).
- 작성자: Unity 클라 3년 + C++ 게임 서버 3년 경력. 서버 프로그래머로 이직 준비 중. 하루 1시간 개발.
- 먼저 읽을 문서: `CLAUDE.md`(구조·컨벤션·불변식), `docs/server-logic-architecture.md`(tick·phase·로그인 흐름), `docs/roadmap.md`(향후 46세션 계획).
- 서버 코드: `Server/THGameServer/`(OutGame 300ms tick, InGame 100ms 룸 tick, Data 샤딩 worker), `Server/THServerCommon/`(SAEA 네트워크, 시간, 설정), `Server/THDummyClient/`(TCP 더미 부하 클라).
- 프로토콜: `Common/Tool/ProtocolGenerator/protos/*.proto`(proto2), 생성물 `generated/*.g.cs`.

## 평가 항목

각 항목을 **A~D 등급 + 근거 3줄 이내 + 개선 제안 1~3개**로 정리하라.

1. **아키텍처**: OutGame/InGame 분리, phase 모델(Event→Prepare→Work→Arrange), 룸 단위 병렬. 과설계인가, 적정한가, 부족한가. 두 tick 서비스가 동형인 것의 장단.
2. **동시성 안전성**: single-writer 규약, PlayerArchive/SessionRoomMap의 lock 없는 접근, PacketQueue 더블버퍼, 룸 Inbox drain 방식. **실제로 race가 가능한 지점**을 찾아라. 특히 세션 종료 흐름(NetDisconnect → ODExitGameSessionReq → DOExitGameSessionAck → Arrange 제거)과 로그인 타임아웃 정리의 경합.
3. **네트워크 계층**: `Session.cs`의 SAEA 사용, 백프레셔, ArrayPool 관리, Close 경로 단일화. CLAUDE.md §5.6 불변식 7개가 코드에서 실제로 지켜지는지 검증하라. 깨진 불변식이 있으면 재현 시나리오를 써라.
4. **실패 경로**: DB 응답 유실, 패킷 파싱 실패, 핸들러 예외, 세션 중간 종료, 중복 패킷. 각 경우 서버 상태가 어떻게 남는지(누수·좀비 세션·archive 불일치).
5. **프로토콜 설계**: proto2 선택, MessageID 99번 필드 패턴, 대역(10000/20000/50000/60000) 라우팅. 클라 대면 패킷 확장 시 문제점.
6. **운영·디버깅 가능성**: 로그 구조화 수준, tick 지연 관측, 세션 수·큐 길이 메트릭 유무, 설정 체계.
7. **테스트**: 단위/통합 테스트 부재 여부, 더미 클라의 검증 범위. 최소로 추가해야 할 테스트 5개.
8. **코드 품질**: 네이밍 일관성, 주석 품질, 파일 구조, 죽은 코드·낡은 TODO.
9. **로드맵 현실성**: `docs/roadmap.md`의 46세션 계획이 하루 1시간에 맞는가. 순서가 잘못된 항목, 빠진 항목, 과대·과소 추정된 세션을 지목하라.
10. **포트폴리오 가치**: 면접관이 이 저장소에서 5분 안에 보게 될 것과 물어볼 질문 5개. 가장 먼저 보강해야 할 한 가지.

## 출력 형식

1. **한 줄 총평** (등급 + 가장 큰 강점 1개 + 가장 큰 리스크 1개)
2. 평가 항목 1~10 (위 형식)
3. **즉시 수정 권고 Top 5** (심각도 순, 각 `경로:줄` + 문제 + 수정 방향)
4. **미확인 사항** (코드만으로 판단 못 한 것)

분량은 A4 3장 이내. 칭찬은 생략하고 문제와 근거에 집중하라.
