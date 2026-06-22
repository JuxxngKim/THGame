namespace TH.Server.Logic;

// OutGameService 전용 입력 큐 (해당 서비스가 소유). Double Buffer 채택 이유:
// 1. tick 모델에서 "일괄 swap → 일괄 처리"가 자연스러움
// 2. List 재사용으로 GC 압박 0
// 3. 게임 OutGame 트래픽(< 1만 PPS)에선 lock 비용 무시 가능
// 향후 부하 측정 후 부족하면 ConcurrentQueue + Interlocked.Exchange로 전환.
public sealed class PacketQueue
{
    private const int InitialCapacity = 256;

    private readonly object _lock = new();
    private List<PacketMessage> _writeBuffer = new(InitialCapacity);
    private List<PacketMessage> _readBuffer = new(InitialCapacity);

    public void Enqueue(long sessionId, int packetId, byte[] payload)
    {
        var msg = new PacketMessage(sessionId, packetId, payload);
        lock (_lock)
        {
            _writeBuffer.Add(msg);
        }
    }

    // 참조 교환 후 이전 write 버퍼를 반환 (O(1)).
    // 호출자가 사용 완료 후 .Clear() 호출 (내부 배열은 유지되어 GC 0).
    public List<PacketMessage> Swap()
    {
        lock (_lock)
        {
            (_writeBuffer, _readBuffer) = (_readBuffer, _writeBuffer);
            return _readBuffer;
        }
    }
}
