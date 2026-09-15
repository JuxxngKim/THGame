using System.Collections.Concurrent;
using Serilog;
using TH.Server.Logic;

namespace TH.Server.Data;

// DBService의 샤드 하나를 맡는 worker. 전용 스레드 1개가 자기 mailbox를 FIFO로 처리한다.
// 같은 sessionID의 요청은 항상 같은 worker로 오므로, 이 단일 스레드 처리가 유저별 요청 순서를 보장한다.
internal sealed class DBWorker
{
    private readonly BlockingCollection<PacketMessage> _mailbox = new();
    private readonly Thread _thread;
    private readonly Action<PacketMessage> _consume;

    public DBWorker(int index, Action<PacketMessage> consume)
    {
        _consume = consume;
        _thread = new Thread(Loop) { IsBackground = true, Name = $"DBWorker-{index}" };
    }

    public void Start() => _thread.Start();

    // OD 요청을 mailbox에 넣는다. DBService.Send가 샤드를 정한 뒤 호출한다. BlockingCollection이라 여러 스레드에서 불려도 안전하다.
    public void Post(in PacketMessage req) => _mailbox.Add(req);

    public void Stop()
    {
        _mailbox.CompleteAdding();
        _thread.Join();
    }

    // 단일 스레드 처리 루프. GetConsumingEnumerable로 도착 순서대로(FIFO) 처리한다.
    // 요청 하나가 실패해도 루프가 죽지 않도록 요청마다 try/catch로 감싼다.
    private void Loop()
    {
        foreach (var req in _mailbox.GetConsumingEnumerable())
        {
            try
            {
                _consume(req);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DBWorker consume exception SessionID={ID} PacketID={PID}", req.SessionID, req.PacketID);
            }
        }
    }
}
