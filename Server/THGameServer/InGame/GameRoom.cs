using System.Collections.Concurrent;
using Serilog;
using TH.Common.Network;
using TH.Server.Game;

namespace TH.Server.Logic;

// 격리 단위 룸("맵 = 룸"). 포탈로 끊긴 독립 시뮬레이션 공간.
//
// 동시성 규약 (가장 중요):
//  - JobQueue 는 외부(Prepare 단일 스레드) ↔ 룸 Work 스레드 간 교차 전달용이라 ConcurrentQueue.
//  - 그 외 룸 내부 상태(Character 컬렉션, Position 등)는 Work phase 에서 이 룸을 잡은 워커 스레드
//    1개만 접근한다 → single-writer 이므로 무락. 외부에서의 상태 변경은 반드시 JobQueue 경유.
public sealed class GameRoom
{
    public RoomID ID { get; }

    // 룸 inbox — 패킷/진입/이탈/룸내부 지연작업이 모두 IRoomJob 으로 들어온다. 교차 스레드라 Concurrent.
    public ConcurrentQueue<IRoomJob> JobQueue { get; } = new();

    // 인원 캡 전제 — 처음엔 List 로 시작. 전원 순회/브로드캐스트가 주 연산이라 List 가 적합.
    private readonly List<Character> _characters = new();
    private readonly Dictionary<long, Character> _bySession = new();

    public GameRoom(RoomID roomID)
    {
        ID = roomID;
    }

    // 룸 1틱 — Work phase 에서 이 룸을 잡은 워커 스레드 1개가 단독 실행. dtMs 는 항상 고정(100ms).
    public void Tick(long dtMs)
    {
        DrainJobs();
        Simulate(dtMs);
    }

    // JobQueue 단일 컨슈머 drain. "큐가 빌 때까지"가 아니라 drain 시작 시점의 Count 만큼만 처리한다:
    //  - 이번 틱 처리 중 룸이 self-enqueue 한 job 은 같은 틱에 재처리되지 않고 다음 틱으로 이월된다
    //    ("다음 틱" 시맨틱 보장 + self-enqueue 무한 drain 방지).
    //  - 안전 근거: 외부(Prepare)의 enqueue 는 Work 시작 전에 끝나고, Work 중에는 이 워커 1개만
    //    enqueue 한다(single-producer-during-work) → 시작 시 Count 가 정확하다.
    private void DrainJobs()
    {
        int count = JobQueue.Count;
        for (int i = 0; i < count; i++)
        {
            if (!JobQueue.TryDequeue(out var job))
                break;

            try
            {
                job.Execute(this);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Room job exception RoomID={RID} Job={Job}", ID, job.GetType().Name);
            }
        }
    }

    // 룸 시뮬레이션 1스텝 — 전투/이동 적분 등. 현재는 골격(인프라 우선).
    private void Simulate(long dtMs)
    {
        _ = dtMs;
    }

    // ====================== 룸 내부 mutate (Work 스레드 단독) ======================

    // 진입 — Character 생성/등록.
    public void AddCharacter(long sessionID)
    {
        if (_bySession.ContainsKey(sessionID))
        {
            Log.Warning("Room enter skipped — already in room RoomID={RID} SessionID={SID}", ID, sessionID);
            return;
        }

        var character = new Character(sessionID);
        _bySession.Add(sessionID, character);
        _characters.Add(character);

        Log.Debug("Character entered RoomID={RID} SessionID={SID}", ID, sessionID);
    }

    // 이탈 — Character 제거.
    public void RemoveCharacter(long sessionID)
    {
        if (!_bySession.Remove(sessionID, out var character))
            return;

        _characters.Remove(character);
        Log.Debug("Character left RoomID={RID} SessionID={SID}", ID, sessionID);
    }

    // 이동(server-authoritative) — 검증 통과 시 position 갱신.
    // 실제 패킷 핸들러(HandlePacket)가 proto 추가 후 이 경로로 들어온다. 현재는 호출 지점 골격.
    public void MoveCharacter(long sessionID, Position target)
    {
        if (!_bySession.TryGetValue(sessionID, out var character))
            return;

        // TODO: server-authoritative 이동 검증(속도/충돌/이동 가능 영역). 통과 시에만 갱신.
        character.Position = target;
    }

    // 네트워크발 InGame 패킷 처리 진입점 — PacketRoomJob 이 호출. proto 무수정 단계라 현재는 골격.
    // 실제 패킷 추가 시 packetID 별 분기 → MoveCharacter 등 권위 처리 후 Broadcast 로 전파.
    public void HandlePacket(in PacketMessage packet)
    {
        _ = packet;
    }

    // 룸 이벤트 전파 — 룸 전원에게 송신.
    public void Broadcast(int packetID, byte[] payload)
    {
        for (int i = 0; i < _characters.Count; i++)
        {
            var session = NetworkManager.Instance.FindSession(_characters[i].SessionID);
            session?.Send(packetID, payload);
        }
    }
}
