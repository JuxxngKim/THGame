using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Serilog;

namespace TH.Common.Network;

public sealed class Session
{
    public const int MaxPacketSize = 65536;
    public const int DefaultRecvBufferSize = 8192;
    public const long MaxPendingSendBytes = 1L * 1024 * 1024;

    private static long _nextID;

    public long SessionID { get; }

    private volatile Socket? _socket;
    private readonly SocketAsyncEventArgs _recvSaea;
    private readonly SocketAsyncEventArgs _sendSaea;

    private byte[] _recvBuffer;
    private int _recvOffset;

    private readonly ConcurrentQueue<byte[]> _sendQueue = new();
    private int _sending;
    private long _pendingSendBytes;
    private int _inflightLength;

    private int _closed;

    public delegate void PacketHandler(Session session, int packetID, ReadOnlySpan<byte> payload);

    // ⚠️ payload는 내부 수신 버퍼의 슬라이스다. 핸들러는 그 자리에서 즉시 디코드해야 하며,
    // span을 잡아두거나 보관하면 안 된다. 다음 수신 때 덮어쓰인다.
    public PacketHandler? OnPacketReceived;
    public Action<Session>? OnDisconnected;

    public Session(Socket socket)
    {
        SessionID = Interlocked.Increment(ref _nextID);
        _socket = socket;

        _recvBuffer = new byte[DefaultRecvBufferSize];

        _recvSaea = new SocketAsyncEventArgs();
        _recvSaea.Completed += OnRecvCompleted;

        _sendSaea = new SocketAsyncEventArgs();
        _sendSaea.Completed += OnSendCompleted;
    }

    // ====================== 수신 ======================

    public void BeginReceive()
    {
        ReceiveLoop();
    }

    private void ReceiveLoop()
    {
        while (true)
        {
            if (Volatile.Read(ref _closed) == 1) return;

            var socket = _socket;
            if (socket is null) return;

            int available = _recvBuffer.Length - _recvOffset;
            if (available <= 0)
            {
                Log.Error("Session {ID} recv buffer full (offset={Off}, size={Size}), forcing close",
                    SessionID, _recvOffset, _recvBuffer.Length);
                Close(notify: true);
                return;
            }

            _recvSaea.SetBuffer(_recvBuffer, _recvOffset, available);

            bool pending;
            try
            {
                pending = socket.ReceiveAsync(_recvSaea);
            }
            catch (ObjectDisposedException)
            {
                HandleDisconnect();
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Session {ID} ReceiveAsync call failed", SessionID);
                HandleDisconnect();
                return;
            }

            if (pending) return;
            if (!ProcessReceiveResult()) return;
        }
    }

    private void OnRecvCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (!ProcessReceiveResult()) return;
        ReceiveLoop();
    }

    private bool ProcessReceiveResult()
    {
        if (_recvSaea.SocketError != SocketError.Success || _recvSaea.BytesTransferred == 0)
        {
            HandleDisconnect();
            return false;
        }

        _recvOffset += _recvSaea.BytesTransferred;
        return ProcessReceivedData();
    }

    private bool ProcessReceivedData()
    {
        int consumed = 0;

        while (true)
        {
            var available = new ReadOnlySpan<byte>(_recvBuffer, consumed, _recvOffset - consumed);

            if (!PacketHeader.TryRead(available, out int length, out int packetID))
                break;

            if (length < PacketHeader.HeaderSize || length > MaxPacketSize)
            {
                Log.Warning("Session {ID} invalid packet length {Length}, forcing close", SessionID, length);
                Close(notify: true);
                return false;
            }

            if (length > _recvBuffer.Length)
            {
                var newBuffer = new byte[length];
                Buffer.BlockCopy(_recvBuffer, 0, newBuffer, 0, _recvOffset);
                _recvBuffer = newBuffer;
                available = new ReadOnlySpan<byte>(_recvBuffer, consumed, _recvOffset - consumed);
            }

            if (available.Length < length)
                break;

            int payloadOffset = consumed + PacketHeader.HeaderSize;
            int payloadLength = length - PacketHeader.HeaderSize;

            // ⚠️ IO 스레드에서 호출된다. 핸들러는 즉시 반환해야 하며(블로킹 금지),
            // payload span을 잡아두거나 보관하면 안 된다. 다음 수신 때 _recvBuffer가 덮어쓰인다.
            // 로직 스레드로 넘기려면 핸들러 안에서 복사한 뒤 큐에 넣는다(GameServerApp 참조).
            var payload = new ReadOnlySpan<byte>(_recvBuffer, payloadOffset, payloadLength);
            try
            {
                OnPacketReceived?.Invoke(this, packetID, payload);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Session {ID} OnPacketReceived handler exception (PacketID={PacketID})", SessionID, packetID);
            }

            consumed += length;
        }

        if (consumed > 0)
        {
            int remaining = _recvOffset - consumed;
            if (remaining > 0)
                Buffer.BlockCopy(_recvBuffer, consumed, _recvBuffer, 0, remaining);
            _recvOffset = remaining;
        }

        return true;
    }

    // ====================== 송신 ======================

    public void Send(int packetID, ReadOnlySpan<byte> payload)
    {
        if (Volatile.Read(ref _closed) == 1) return;

        int totalLength = PacketHeader.HeaderSize + payload.Length;

        if (totalLength > MaxPacketSize)
        {
            Log.Warning("Session {ID} send packet too large {Size}, forcing close", SessionID, totalLength);
            Close(notify: true);
            return;
        }

        // 1) 백프레셔를 먼저 확인한다(Rent 전). 순서를 바꾸면 안 된다.
        long after = Interlocked.Add(ref _pendingSendBytes, totalLength);
        if (after > MaxPendingSendBytes)
        {
            Interlocked.Add(ref _pendingSendBytes, -totalLength);
            Log.Warning("Session {ID} send buffer overflow ({Bytes}B), forcing close", SessionID, after);
            Close(notify: true);
            return;
        }

        // 2) Rent + Write
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        PacketHeader.Write(buffer, totalLength, packetID);
        payload.CopyTo(buffer.AsSpan(PacketHeader.HeaderSize, payload.Length));

        _sendQueue.Enqueue(buffer);

        // 3) 큐에 넣은 뒤 _closed를 다시 확인한다. Close와의 race로 ArrayPool 버퍼가 새는 것을 막는다.
        if (Volatile.Read(ref _closed) == 1)
        {
            DrainSendQueue();
            return;
        }

        FlushSendQueue();
    }

    private void FlushSendQueue()
    {
        if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0)
            return;

        SendLoop();
    }

    private void SendLoop()
    {
        while (true)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                DrainSendQueue();
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            if (!_sendQueue.TryDequeue(out var buffer))
            {
                Interlocked.Exchange(ref _sending, 0);
                if (_sendQueue.IsEmpty) return;
                if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;
                continue;
            }

            int length = GetUsedLength(buffer);
            _inflightLength = length;
            _sendSaea.SetBuffer(buffer, 0, length);

            var socket = _socket;
            if (socket is null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Add(ref _pendingSendBytes, -length);
                _inflightLength = 0;
                DrainSendQueue();
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            bool pending;
            try
            {
                pending = socket.SendAsync(_sendSaea);
            }
            catch (ObjectDisposedException)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Add(ref _pendingSendBytes, -length);
                _inflightLength = 0;
                HandleDisconnect();
                Interlocked.Exchange(ref _sending, 0);
                return;
            }
            catch (Exception ex)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Add(ref _pendingSendBytes, -length);
                _inflightLength = 0;
                Log.Error(ex, "Session {ID} SendAsync call failed", SessionID);
                HandleDisconnect();
                Interlocked.Exchange(ref _sending, 0);
                return;
            }

            if (pending) return;
            if (!ProcessSendResult()) return;
        }
    }

    private void OnSendCompleted(object? sender, SocketAsyncEventArgs e)
    {
        if (!ProcessSendResult()) return;
        SendLoop();
    }

    private bool ProcessSendResult()
    {
        var buffer = _sendSaea.Buffer;
        int length = _inflightLength;
        _inflightLength = 0;

        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
        Interlocked.Add(ref _pendingSendBytes, -length);

        if (_sendSaea.SocketError != SocketError.Success)
        {
            HandleDisconnect();
            Interlocked.Exchange(ref _sending, 0);
            return false;
        }

        return true;
    }

    // Rent한 버퍼는 payload보다 클 수 있어 실제 사용 길이를 따로 알 수 없다.
    // 그래서 buffer 전체 길이가 아니라 헤더에 적힌 패킷 길이를 읽어 반환한다.
    // SendLoop는 이 값을 _sendSaea.SetBuffer의 length로 쓴다.
    private static int GetUsedLength(byte[] buffer)
    {
        PacketHeader.TryRead(buffer, out int length, out _);
        return length;
    }

    private void DrainSendQueue()
    {
        while (_sendQueue.TryDequeue(out var buf))
        {
            int len = GetUsedLength(buf);
            ArrayPool<byte>.Shared.Return(buf);
            if (len > 0) Interlocked.Add(ref _pendingSendBytes, -len);
        }
    }

    // ====================== 종료 ======================

    public void Close(bool notify = false)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        CloseResources();
        if (notify) OnDisconnected?.Invoke(this);
    }

    private void HandleDisconnect()
    {
        Close(notify: true);
    }

    private void CloseResources()
    {
        var socket = _socket;
        if (socket is not null)
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch { }
            try { socket.Close(); } catch { }
            _socket = null;
        }

        // SAEA는 Dispose하지 않는다. Socket.Close 뒤에도 in-flight 콜백이 OperationAborted로
        // 도착할 수 있어 그 race를 피한다. GC가 회수한다.

        DrainSendQueue();
    }
}
