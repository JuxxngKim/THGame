using System.Collections.Concurrent;
using Serilog;
using TH.Server.Logic;

namespace TH.Server.Data;

// DBService 의 단일 샤드 worker. 전용 스레드 1개가 자기 mailbox 를 FIFO 로 순차 소비한다.
// 같은 sessionId 의 요청은 항상 동일 worker 로 라우팅되므로(샤딩), 이 단일 스레드 소비가
// "유저별 요청 순서 보장" 을 성립시킨다.
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

    // AD 요청 적재 — DBService.Send 가 라우팅 후 호출. 여러 스레드에서 호출돼도 BlockingCollection 은 스레드 안전.
    public void Post(in PacketMessage req) => _mailbox.Add(req);

    public void Stop()
    {
        _mailbox.CompleteAdding();
        _thread.Join();
    }

    // 단일 스레드 소비 루프 — GetConsumingEnumerable 로 도착 순서대로(FIFO) 처리.
    // 한 요청의 실패가 루프를 죽이지 않도록 요청마다 try/catch 로 격리.
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
                Log.Error(ex, "DBWorker consume exception SessionId={Id} PacketId={Pid}", req.SessionId, req.PacketId);
            }
        }
    }
}
