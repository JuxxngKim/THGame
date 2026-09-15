namespace TH.Server.Logic;

// tick 서비스의 입력 큐(서비스가 소유). Double Buffer를 택한 이유:
// 1. tick 모델에서는 "한 번에 swap하고 한 번에 처리"가 자연스럽다.
// 2. List를 재사용해 GC 부담이 없다.
// 3. OutGame 트래픽(1만 PPS 미만)에서는 lock 비용을 무시할 수 있다.
// 부하 측정 후 부족하면 ConcurrentQueue + Interlocked.Exchange로 바꾼다.
public sealed class PacketQueue
{
    private const int InitialCapacity = 256;

    private readonly object _lock = new();
    private List<PacketMessage> _writeBuffer = new(InitialCapacity);
    private List<PacketMessage> _readBuffer = new(InitialCapacity);

    public void Enqueue(long sessionID, int packetID, byte[] payload)
    {
        var msg = new PacketMessage(sessionID, packetID, payload);
        lock (_lock)
        {
            _writeBuffer.Add(msg);
        }
    }

    // 두 버퍼의 참조를 바꾸고 이전 write 버퍼를 반환한다(O(1)).
    // 호출자가 다 쓴 뒤 .Clear()를 호출한다. 내부 배열은 유지되어 GC가 없다.
    public List<PacketMessage> Swap()
    {
        lock (_lock)
        {
            (_writeBuffer, _readBuffer) = (_readBuffer, _writeBuffer);
            return _readBuffer;
        }
    }
}
