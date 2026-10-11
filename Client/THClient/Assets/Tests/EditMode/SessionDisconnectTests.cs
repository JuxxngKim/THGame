using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using TH.Network;

namespace TH.Tests
{
    // 단일 종료 경로 검증. 어떤 경로로 끊겨도 OnDisconnected 가 Poll 안에서 정확히 1회만 불리는지 본다.
    public class SessionDisconnectTests
    {
        private TcpListener _listener;
        private Session _session;
        private TcpClient _server;
        private int _notifyCount;
        private EDisconnectReason _reason;

        [SetUp]
        public void SetUp()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _session = new Session();
            _session.OnDisconnected = reason =>
            {
                _notifyCount++;
                _reason = reason;
            };
            _session.Connect("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port);
            _server = _listener.AcceptTcpClient();
            _notifyCount = 0;
            _reason = EDisconnectReason.None;
        }

        [TearDown]
        public void TearDown()
        {
            _session.Close();
            _server.Close();
            _listener.Stop();
        }

        [Test]
        public void ServerClose_NotifiesOnce_WithRemoteClosed()
        {
            _server.Close();
            Assert.IsTrue(SessionTests.WaitUntil(() => !_session.IsConnected), "서버 종료를 감지하지 못함");

            // 끊긴 뒤 여러 번 Poll 해도 알림은 1번이어야 한다.
            for (int i = 0; i < 3; i++)
                _session.Poll(_ => { });

            Assert.AreEqual(1, _notifyCount);
            Assert.AreEqual(EDisconnectReason.RemoteClosed, _reason);
        }

        [Test]
        public void LocalClose_NotifiesOnce_AndServerSeesClose()
        {
            _session.Close();
            for (int i = 0; i < 3; i++)
                _session.Poll(_ => { });

            Assert.AreEqual(1, _notifyCount);
            Assert.AreEqual(EDisconnectReason.LocalClose, _reason);
            Assert.IsFalse(_session.Send(102, new byte[] { 1 }));

            // 소켓이 실제로 닫혔으면 서버 쪽 Read 가 0 을 돌려준다(FIN 도착).
            _server.ReceiveTimeout = 2000;
            Assert.AreEqual(0, _server.GetStream().Read(new byte[16], 0, 16));
        }

        [Test]
        public void CloseTwice_RacingServerClose_NotifiesOnce()
        {
            var serverClose = new Thread(() => _server.Close());
            serverClose.Start();
            _session.Close();
            _session.Close();
            serverClose.Join();

            // 수신 스레드가 뒤늦게 끝나도 알림이 또 오지 않는지 잠시 계속 Poll 한다.
            for (int i = 0; i < 20; i++)
            {
                _session.Poll(_ => { });
                Thread.Sleep(10);
            }

            Assert.AreEqual(1, _notifyCount);
            // 어느 쪽이 먼저 닫았는지는 타이밍에 따라 다르다.
            Assert.That(_reason, Is.EqualTo(EDisconnectReason.LocalClose).Or.EqualTo(EDisconnectReason.RemoteClosed));
        }

        [Test]
        public void PacketsBeforeServerClose_AreHandledBeforeNotification()
        {
            var events = new List<string>();
            _session.OnDisconnected = reason => events.Add("disconnect");

            byte[] first = PacketAssemblerTests.MakePacket(10003, new byte[] { 1 });
            byte[] second = PacketAssemblerTests.MakePacket(60001, new byte[] { 2 });
            _server.GetStream().Write(first, 0, first.Length);
            _server.GetStream().Write(second, 0, second.Length);
            _server.Close();

            // 끊김이 확정될 때까지 Poll 하지 않고 기다린다. 그래야 한 번의 Poll 에 패킷과 끊김이 같이 들어 있다.
            Assert.IsTrue(SessionTests.WaitUntil(() => !_session.IsConnected), "서버 종료를 감지하지 못함");
            _session.Poll(packet => events.Add("packet:" + packet.PacketID));

            CollectionAssert.AreEqual(new[] { "packet:10003", "packet:60001", "disconnect" }, events);
        }

        [Test]
        public void SendAfterServerClose_ReturnsFalse()
        {
            _server.Close();
            Assert.IsTrue(SessionTests.WaitUntil(() => !_session.IsConnected), "서버 종료를 감지하지 못함");

            Assert.IsFalse(_session.Send(102, new byte[] { 1 }));
        }

        [Test]
        public void InvalidHeader_HandlesEarlierPacket_ThenNotifiesInvalidHeader()
        {
            var handled = new List<PacketMessage>();
            byte[] valid = PacketAssemblerTests.MakePacket(10003, new byte[] { 1 });

            // length 가 헤더 크기(8)보다 작은 헤더. 스트림이 깨진 것으로 보고 끊어야 한다.
            var invalid = new byte[PacketHeader.HeaderSize];
            PacketHeader.Write(invalid, 3, 10003);

            _server.GetStream().Write(valid, 0, valid.Length);
            _server.GetStream().Write(invalid, 0, invalid.Length);

            Assert.IsTrue(SessionTests.WaitUntil(() => !_session.IsConnected), "잘못된 헤더로 끊지 않음");
            _session.Poll(handled.Add);

            Assert.AreEqual(1, handled.Count);
            Assert.AreEqual(10003, handled[0].PacketID);
            Assert.AreEqual(1, _notifyCount);
            Assert.AreEqual(EDisconnectReason.InvalidHeader, _reason);
        }
    }
}
