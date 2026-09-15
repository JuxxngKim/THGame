using System.Collections.Concurrent;
using Google.Protobuf;
using Serilog;
using Th;
using TH.Common.Network;
using TH.Server.Common;
using TH.Server.Game;

namespace TH.Server.Logic;

// 독립 시뮬레이션 단위인 룸("맵 = 룸"). 구조는 docs/server-logic-architecture.md §6.4 참조.
//
// 동시성 규약:
//  - Inbox는 Prepare(단일 스레드)가 넣고 룸 Work 스레드가 꺼내는 스레드 간 전달 큐라 ConcurrentQueue를 쓴다.
//  - 룸 내부 상태(Character, Position 등)는 Work 단계에서 이 룸을 맡은 워커 스레드 하나만 바꾼다(single-writer, lock 없음).
//    밖에서 상태를 바꾸려면 반드시 Inbox 패킷으로 보낸다. 공유 상태(SessionRoomMap/RoomRepository)는 Prepare가 담당한다.
public sealed class GameRoom
{
    public RoomID ID { get; }

    // 룸 inbox. 입장/퇴장/게임플레이 패킷이 PacketMessage로 들어온다. 스레드 간 전달이라 Concurrent.
    public ConcurrentQueue<PacketMessage> Inbox { get; } = new();

    // 인원 상한이 있다는 전제로 List를 쓴다. 주 연산이 전원 순회/브로드캐스트라 List가 맞다.
    private readonly List<Character> _characters = new();
    private readonly Dictionary<long, Character> _bySession = new();

    // ====================== 패킷 핸들러 테이블 (static 공유) ======================

    // packetID → dispatch 델리게이트(공통 테이블). static 생성자에서만 채우고 이후 읽기 전용.
    private static readonly PacketHandlerTable<GameRoom> Table = new();

    static GameRoom()
    {
        // 입장/퇴장은 SessionID만 필요하므로(룸은 이미 정해짐) body(msg)는 쓰지 않는다.
        Table.Register<OIEnterReq>((int)EMessageID.OiEnterReq, (room, pkt, msg) => room.OnEnter(pkt.SessionID));
        Table.Register<OILeaveReq>((int)EMessageID.OiLeaveReq, (room, pkt, msg) => room.RemoveCharacter(pkt.SessionID));
        Table.Register<OIExitGameSessionReq>((int)EMessageID.OiExitGameSessionReq, (room, pkt, msg) => room.RemoveCharacter(pkt.SessionID));
    }

    public GameRoom(RoomID roomID)
    {
        ID = roomID;
    }

    // 룸 1틱. Work 단계에서 이 룸을 맡은 워커 스레드 하나만 실행한다. dtMs는 지난 tick 이후 실제 경과 시간(가변).
    public void Tick(long dtMs)
    {
        DrainInbox();
        Simulate(dtMs);
    }

    // Inbox drain(소비자 1개). "큐가 빌 때까지"가 아니라 시작 시점의 Count만큼만 처리한다.
    //  - 이번 틱 처리 중 룸이 스스로 넣은 패킷은 다음 틱으로 넘긴다. "다음 틱" 의미를 지키고 무한 drain을 막기 위해서다.
    //  - Count를 믿을 수 있는 이유: Prepare의 enqueue는 Work 시작 전에 끝나고, Work 중에는 이 워커 하나만 넣는다.
    private void DrainInbox()
    {
        int count = Inbox.Count;
        for (int i = 0; i < count; i++)
        {
            if (!Inbox.TryDequeue(out var packet))
                break;

            try
            {
                if (!Table.Dispatch(this, packet))
                    Log.Debug("Unhandled room packet RoomID={RID} PacketID={PID}", ID, packet.PacketID);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Room packet exception RoomID={RID} PacketID={PID}", ID, packet.PacketID);
            }
        }
    }

    // 룸 시뮬레이션 1스텝(전투/이동 계산 등). 현재는 골격만 있다.
    private void Simulate(long dtMs)
    {
        _ = dtMs;
    }

    // ====================== 룸 내부 상태 변경 (Work 스레드 단독) ======================

    // 입장. Character를 만들어 등록한다. 이미 룸에 있으면 null을 반환해 호출부가 알림을 건너뛰게 한다.
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

    // 퇴장. Character를 제거한다.
    public void RemoveCharacter(long sessionID)
    {
        if (!_bySession.Remove(sessionID, out var character))
            return;

        _characters.Remove(character);
        Log.Debug("Character left RoomID={RID} SessionID={SID}", ID, sessionID);
    }

    // 이동(server-authoritative). 검증을 통과하면 position을 갱신한다.
    // proto 추가 후 이동 패킷을 Table.Register로 등록하면 이 경로로 들어온다. 현재는 호출 지점만 있다.
    public void MoveCharacter(long sessionID, Position target)
    {
        if (!_bySession.TryGetValue(sessionID, out var character))
            return;

        // TODO: server-authoritative 이동 검증(속도/충돌/이동 가능 영역). 통과할 때만 갱신.
        character.Position = target;
    }

    // 입장 처리. Character 생성에 성공하면 클라(ICEnterNoti)와 OutGame(IOEnterAck)에 결과를 알린다.
    private void OnEnter(long sessionID)
    {
        var character = AddCharacter(sessionID);
        if (character is null)
            return;

        // 클라에 스폰 정보를 직접 알린다(InGame → Client). 룸 Work 스레드에서 바로 보내도 Session.Send는 스레드 안전하다.
        var noti = new ICEnterNoti
        {
            SessionID = sessionID,
            Position  = new MPosition { X = character.Position.X, Y = character.Position.Y, Z = character.Position.Z },
        };
        NetworkManager.Instance.FindSession(sessionID)?.Send((int)EMessageID.IcEnterNoti, noti.ToByteArray());

        // OutGame에 입장 확정 ack(InGame → OutGame). State를 InField로 바꾸는 건 OutGame Player가 한다.
        var ack = new IOEnterAck { RoomID = ID.Value };
        OutGameService.Instance.EnqueuePacket(sessionID, (int)EMessageID.IoEnterAck, ack.ToByteArray());
    }

    // 룸 전원에게 송신.
    public void Broadcast(int packetID, byte[] payload)
    {
        for (int i = 0; i < _characters.Count; i++)
        {
            var session = NetworkManager.Instance.FindSession(_characters[i].SessionID);
            session?.Send(packetID, payload);
        }
    }
}
