using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;

namespace TH.Network
{
    // 서버와의 TCP 연결 하나. 수신은 전용 스레드가 하고, 완성된 패킷을 ConcurrentQueue 에 넣는다.
    // 메인 스레드는 TryDequeue 로 꺼내 처리한다(수신 스레드 → 큐 → 메인 스레드, 단일 경로).
    // 수신 스레드는 큐에 넣는 일만 한다. Unity API 는 메인 스레드에서만 부를 수 있기 때문이다.
    //
    // 아직 없는 것: 종료를 정확히 1회 알리는 단일 종료 경로와 Play 종료 시 정리(로드맵 세션 6).
    public sealed class TcpConnection
    {
        private const int ReceiveChunkSize = 8192;

        private readonly ConcurrentQueue<ReceivedPacket> _received = new ConcurrentQueue<ReceivedPacket>();
        private readonly Action<ReceivedPacket> _enqueue;
        private readonly object _sendLock = new object();
        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _connected;

        public TcpConnection()
        {
            // 패킷마다 델리게이트를 새로 만들지 않도록 한 번만 만들어 둔다.
            _enqueue = _received.Enqueue;
        }

        public bool IsConnected => _connected;

        // 동기 접속. 실패하면 SocketException 을 던진다. 인스턴스 하나는 한 번만 접속한다.
        // 주의: 접속이 끝날 때까지 호출한 스레드를 막는다. 원격 서버에 메인 스레드에서 붙으면 화면이 멈출 수 있다.
        public void Connect(string host, int port)
        {
            if (_socket != null)
                throw new InvalidOperationException("TcpConnection is single-use. Create a new instance.");

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            socket.Connect(host, port);

            _socket = socket;
            _connected = true;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "TH.Network.Receive" };
            _receiveThread.Start();
        }

        // 패킷 하나를 보낸다. 헤더와 payload 를 한 버퍼로 묶어 한 번에 보낸다.
        // 메인 스레드에서만 부른다고 가정한다. 그래도 여러 스레드가 부르면 패킷이 섞이지 않도록 lock 을 건다.
        // 동기 송신이라 OS 송신 버퍼가 꽉 차면(서버가 못 읽는 상황) 호출한 스레드가 잠깐 막힐 수 있다.
        // 연결이 끊겨 있거나 보내는 중에 끊기면 false.
        public bool Send(int packetID, byte[] payload)
        {
            int total = PacketHeader.HeaderSize + payload.Length;
            if (total > PacketAssembler.MaxPacketSize)
                throw new ArgumentException($"Packet too large: {total} bytes (max {PacketAssembler.MaxPacketSize})");
            if (!_connected)
                return false;

            var buffer = new byte[total];
            PacketHeader.Write(buffer, total, packetID);
            Buffer.BlockCopy(payload, 0, buffer, PacketHeader.HeaderSize, payload.Length);

            try
            {
                lock (_sendLock)
                {
                    int sent = 0;
                    while (sent < total)
                        sent += _socket.Send(buffer, sent, total - sent, SocketFlags.None);
                }
                return true;
            }
            catch (SocketException)
            {
                Close();
                return false;
            }
            catch (ObjectDisposedException)
            {
                Close();
                return false;
            }
        }

        public bool Send(int packetID, IMessage message) => Send(packetID, message.ToByteArray());

        // 메인 스레드에서 호출한다. 수신된 패킷을 도착 순서대로 하나씩 꺼낸다.
        public bool TryDequeue(out ReceivedPacket packet) => _received.TryDequeue(out packet);

        // 소켓을 닫는다. 수신 스레드는 막혀 있던 Receive 에서 예외로 빠져나와 종료한다.
        public void Close()
        {
            _connected = false;
            _socket?.Close();
        }

        private void ReceiveLoop()
        {
            var assembler = new PacketAssembler();
            var chunk = new byte[ReceiveChunkSize];
            try
            {
                while (true)
                {
                    int read = _socket.Receive(chunk, 0, chunk.Length, SocketFlags.None);
                    if (read == 0)
                        break; // 서버가 연결을 닫았다.

                    if (!assembler.Append(chunk, 0, read, _enqueue))
                        break; // 길이가 잘못된 헤더. 스트림이 깨졌으니 더 읽지 않는다.
                }
            }
            catch (SocketException)
            {
                // 연결 끊김, 또는 Close() 로 Receive 가 중단된 경우.
            }
            catch (ObjectDisposedException)
            {
                // Close() 가 먼저 소켓을 정리한 경우.
            }
            finally
            {
                _connected = false;
                _socket.Close();
            }
        }
    }
}
