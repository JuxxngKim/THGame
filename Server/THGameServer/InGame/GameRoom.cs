using System.Collections.Concurrent;
using Google.Protobuf;
using Serilog;
using Th;
using TH.Common.Network;
using TH.Server.Game;

namespace TH.Server.Logic;

// 격리 단위 룸("맵 = 룸"). 포탈로 끊긴 독립 시뮬레이션 공간.
//
// 동시성 규약 (가장 중요):
//  - Inbox 는 외부(Prepare 단일 스레드)가 enqueue, 룸 Work 스레드가 drain 하는 교차 전달용이라
//    ConcurrentQueue. 담기는 단위는 원시 패킷(PacketMessage) — 진입/이탈/게임플레이가 모두 패킷이다.
//  - 룸 내부 상태(Character 컬렉션, Position 등)는 Work phase 에서 이 룸을 잡은 워커 스레드 1개만
//    mutate 한다 → single-writer 이므로 무락. 외부의 상태 변경 요청은 반드시 Inbox 패킷으로 전달.
//    (SessionRoomMap/RoomRepository 같은 공유 상태 변경은 룸이 아니라 Prepare 가 담당.)
public sealed class GameRoom
{
    public RoomID ID { get; }

    // 룸 inbox — 진입/이탈/게임플레이 패킷이 PacketMessage 로 들어온다. 교차 스레드라 Concurrent.
    public ConcurrentQueue<PacketMessage> Inbox { get; } = new();

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
        DrainInbox();
        Simulate(dtMs);
    }

    // Inbox 단일 컨슈머 drain. "큐가 빌 때까지"가 아니라 drain 시작 시점의 Count 만큼만 처리한다:
    //  - 이번 틱 처리 중 룸이 self-enqueue 한 패킷은 같은 틱에 재처리되지 않고 다음 틱으로 이월된다
    //    ("다음 틱" 시맨틱 보장 + self-enqueue 무한 drain 방지).
    //  - 안전 근거: 외부(Prepare)의 enqueue 는 Work 시작 전에 끝나고, Work 중에는 이 워커 1개만
    //    enqueue 한다(single-producer-during-work) → 시작 시 Count 가 정확하다.
    private void DrainInbox()
    {
        int count = Inbox.Count;
        for (int i = 0; i < count; i++)
        {
            if (!Inbox.TryDequeue(out var packet))
                break;

            try
            {
                HandlePacket(packet);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Room packet exception RoomID={RID} PacketID={PID}", ID, packet.PacketID);
            }
        }
    }

    // 룸 시뮬레이션 1스텝 — 전투/이동 적분 등. 현재는 골격(인프라 우선).
    private void Simulate(long dtMs)
    {
        _ = dtMs;
    }

    // ====================== 룸 내부 mutate (Work 스레드 단독) ======================

    // 진입 — Character 생성/등록. 중복(이미 룸에 있음)이면 null 을 반환해 호출부가 통지를 건너뛰게 한다.
    public Character? AddCharacter(long sessionID)
    {
        if (_bySession.ContainsKey(sessionID))
        {
            Log.Warning("Room enter skipped — already in room RoomID={RID} SessionID={SID}", ID, sessionID);
            return null;
        }

        var character = new Character(sessionID);
        _bySession.Add(sessionID, character);
        _characters.Add(character);

        Log.Debug("Character entered RoomID={RID} SessionID={SID}", ID, sessionID);
        return character;
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

    // Inbox drain(Work phase)에서 패킷 1건을 packetID 별로 분기. 룸 내부 상태 mutate 는 여기서만.
    // 진입/이탈은 SessionID 만 필요(룸은 이미 확정)하므로 body 파싱 없이 처리한다.
    // 게임플레이(이동/스킬/공격)는 proto 추가 시 여기에 분기 → MoveCharacter 등 권위 처리 후 Broadcast.
    public void HandlePacket(in PacketMessage packet)
    {
        switch (packet.PacketID)
        {
            case (int)EMessageID.OiEnterReq:
                OnEnter(packet.SessionID);
                break;

            case (int)EMessageID.OiLeaveReq:
                RemoveCharacter(packet.SessionID);
                break;

            default:
                Log.Debug("Unhandled room packet RoomID={RID} PacketID={PID}", ID, packet.PacketID);
                break;
        }
    }

    // 진입 처리 — Character 생성 성공 시 클라(ICEnterNoti)와 OutGame(IOEnterAck)에 결과를 통지한다.
    private void OnEnter(long sessionID)
    {
        var character = AddCharacter(sessionID);
        if (character is null)
            return;

        // 클라에 스폰 정보 직접 통지(InGame → Client). 룸 Work 스레드에서 직접 송신 — Session.Send 는 스레드 안전.
        var noti = new ICEnterNoti
        {
            SessionID = sessionID,
            Position  = new MPosition { X = character.Position.X, Y = character.Position.Y, Z = character.Position.Z },
        };
        NetworkManager.Instance.FindSession(sessionID)?.Send((int)EMessageID.IcEnterNoti, noti.ToByteArray());

        // OutGame 에 입장 확정 ack(InGame → OutGame). State=InField 전이는 OutGame Player 가 수행.
        var ack = new IOEnterAck { RoomID = ID.Value };
        OutGameService.Instance.EnqueuePacket(sessionID, (int)EMessageID.IoEnterAck, ack.ToByteArray());
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
