using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;

namespace TH.Network
{
    // 서버와의 TCP 연결 하나. 수신은 전용 스레드가 하고, 완성된 패킷을 ConcurrentQueue 에 넣는다.
    // 메인 스레드는 Poll 로 꺼내 처리한다(수신 스레드 → 큐 → 메인 스레드, 단일 경로).
    // 수신 스레드는 큐에 넣는 일만 한다. Unity API 는 메인 스레드에서만 부를 수 있기 때문이다.
    //
    // 종료: Close, 수신 스레드 종료, Send 실패가 모두 CloseWith 하나로 모인다. CAS 로 처음 1회만 소켓을 닫고 이유를 남긴다.
    // 알림: OnDisconnected 는 메인 스레드의 Poll 안에서만 정확히 1회 부른다. 끊기 전에 도착한 패킷이 알림보다 먼저 처리된다.
    //
    // 서버 Session 과 이름은 같지만 알림을 부르는 스레드가 다르다.
    // 서버는 닫은 스레드(IO 스레드)에서 바로 부르고, 클라는 메인 스레드의 Poll 까지 미룬다.
    public sealed class Session
    {
        private const int ReceiveChunkSize = 8192;

        // 열려 있는 연결 목록. 에디터 Play 종료나 도메인 리로드 때 CloseAll 로 한꺼번에 닫는 안전망이다.
        private static readonly HashSet<Session> _live = new HashSet<Session>();

        private readonly ConcurrentQueue<PacketMessage> _received = new ConcurrentQueue<PacketMessage>();
        private readonly Action<PacketMessage> _enqueue;
        private readonly object _sendLock = new object();
        private Socket _socket;
        private Thread _receiveThread;

        // 0(None) 이면 열려 있다. CAS 게이트이자 끊긴 이유다. 먼저 닫은 쪽의 값이 남는다.
        private int _closeReason;

        // 메인 스레드 전용. OnDisconnected 를 이미 불렀는지.
        private bool _notified;

        // 끊김 알림. Poll 안에서 메인 스레드로 정확히 1회 부른다. 직접 Close() 한 경우도 LocalClose 로 알린다.
        // Poll 을 계속 부르는 동안에만 전달된다. 예를 들어 OnDestroy 에서 Close 하면 그 뒤로 Poll 이 없으니 알림도 없다.
        public Action<EDisconnectReason> OnDisconnected;

        public Session()
        {
            // 패킷마다 델리게이트를 새로 만들지 않도록 한 번만 만들어 둔다.
            _enqueue = _received.Enqueue;
        }

        public bool IsConnected => _socket != null && Volatile.Read(ref _closeReason) == 0;

        // 동기 접속. 실패하면 SocketException 을 던진다. 인스턴스 하나는 한 번만 접속한다.
        // 주의: 접속이 끝날 때까지 호출한 스레드를 막는다. 원격 서버에 메인 스레드에서 붙으면 화면이 멈출 수 있다.
        public void Connect(string host, int port)
        {
            if (_socket != null || Volatile.Read(ref _closeReason) != 0)
                throw new InvalidOperationException("Session is single-use. Create a new instance.");

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Connect(host, port);
            }
            catch
            {
                socket.Close();
                throw;
            }

            _socket = socket;
            lock (_live)
                _live.Add(this);
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
            if (!IsConnected)
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
                CloseWith(EDisconnectReason.SendFailed);
                return false;
            }
            catch (ObjectDisposedException)
            {
                // 다른 경로가 먼저 소켓을 닫은 경우. CAS 에서 지므로 먼저 닫은 쪽의 이유가 남는다.
                CloseWith(EDisconnectReason.SendFailed);
                return false;
            }
        }

        public bool Send(int packetID, IMessage message) => Send(packetID, message.ToByteArray());

        // 메인 스레드 전용. 받은 패킷을 도착 순서대로 handle 에 넘기고, 끊겼으면 마지막에 OnDisconnected 를 1회 부른다.
        // 순서가 중요하다: 이유를 먼저 읽고 → 큐를 비우고 → 아까 읽은 이유가 있으면 알린다.
        // 수신 스레드는 마지막 패킷을 큐에 넣은 뒤에 CAS 하므로, 이유가 보였다면 그 전 패킷은 이미 큐에 있다.
        // 반대로 큐를 비운 뒤에 이유를 읽으면, 그 사이에 들어온 마지막 패킷이 알림 뒤로 밀리거나 버려진다.
        // 처리한 패킷 수를 돌려준다.
        public int Poll(Action<PacketMessage> handle)
        {
            if (_notified)
                return 0;

            var reason = (EDisconnectReason)Volatile.Read(ref _closeReason);
            int count = 0;
            while (_received.TryDequeue(out var packet))
            {
                handle(packet);
                count++;
            }

            if (reason != EDisconnectReason.None)
            {
                _notified = true;
                OnDisconnected?.Invoke(reason);
            }
            return count;
        }

        // 연결을 닫는다. 여러 번 불러도, 다른 종료와 겹쳐도 안전하다. 알림은 다음 Poll 에서 LocalClose 로 간다.
        public void Close() => CloseWith(EDisconnectReason.LocalClose);

        // 열려 있는 연결을 모두 닫는다. 에디터 Play 종료·도메인 리로드 때 쓰는 안전망이다(NetworkEditorCleanup).
        public static void CloseAll()
        {
            // CloseWith 가 목록에서 자신을 빼므로, 복사본을 만들어 lock 밖에서 닫는다.
            Session[] snapshot;
            lock (_live)
            {
                snapshot = new Session[_live.Count];
                _live.CopyTo(snapshot);
            }

            foreach (var session in snapshot)
                session.Close();
        }

        // 모든 종료 경로(Close, 수신 스레드 종료, Send 실패)가 여기로 온다. 어느 스레드에서 불려도 처음 1회만 정리한다.
        // 서버 Session.Close 의 _closed 와 같은 방식이다. 알림은 여기서 하지 않고 메인 스레드의 Poll 에 맡긴다.
        private void CloseWith(EDisconnectReason reason)
        {
            if (Interlocked.CompareExchange(ref _closeReason, (int)reason, 0) != 0)
                return;

            lock (_live)
                _live.Remove(this);

            // Connect 전에 Close 하면 소켓이 없다.
            var socket = _socket;
            if (socket == null)
                return;

            // 소켓을 닫으면 막혀 있던 수신 스레드의 Receive 가 예외로 빠져나와 스레드가 끝난다.
            try { socket.Shutdown(SocketShutdown.Both); } catch { }
            try { socket.Close(); } catch { }
        }

        private void ReceiveLoop()
        {
            var assembler = new PacketAssembler();
            var chunk = new byte[ReceiveChunkSize];
            var reason = EDisconnectReason.SocketError;
            try
            {
                while (true)
                {
                    int read = _socket.Receive(chunk, 0, chunk.Length, SocketFlags.None);
                    if (read == 0)
                    {
                        reason = EDisconnectReason.RemoteClosed;
                        break;
                    }

                    if (!assembler.Append(chunk, 0, read, _enqueue))
                    {
                        // 길이가 잘못된 헤더. 스트림이 깨졌으니 더 읽지 않는다.
                        reason = EDisconnectReason.InvalidHeader;
                        break;
                    }
                }
            }
            catch (SocketException)
            {
                // 연결 끊김, 또는 Close() 로 Receive 가 중단된 경우.
                // 후자는 CAS 에서 지므로 LocalClose 가 남는다.
            }
            catch (ObjectDisposedException)
            {
                // Close() 가 먼저 소켓을 정리한 경우.
            }
            finally
            {
                // 마지막 패킷을 큐에 넣은 뒤에 닫는다. Poll 의 순서 보장이 이 순서에 기댄다.
                CloseWith(reason);
            }
        }
    }
}
