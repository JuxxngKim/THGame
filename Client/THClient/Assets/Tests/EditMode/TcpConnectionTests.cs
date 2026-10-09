using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using TH.Network;

namespace TH.Tests
{
    // 실제 소켓으로 수신 스레드 → 큐 경로를 검증한다. 게임 서버 대신 테스트 안에서 루프백 리스너를 띄운다.
    public class TcpConnectionTests
    {
        private const int TimeoutMs = 2000;

        [Test]
        public void ReceivesPacketsInOrder_AndDetectsServerClose()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var connection = new TcpConnection();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                connection.Connect("127.0.0.1", port);
                Assert.IsTrue(connection.IsConnected);

                using (TcpClient server = listener.AcceptTcpClient())
                {
                    // 두 패킷을 한 번에 보낸다. 클라는 순서대로 2개를 꺼내야 한다.
                    byte[] first = PacketAssemblerTests.MakePacket(10003, new byte[] { 1 });
                    byte[] second = PacketAssemblerTests.MakePacket(60001, new byte[] { 2, 3 });
                    server.GetStream().Write(first, 0, first.Length);
                    server.GetStream().Write(second, 0, second.Length);

                    var received = new List<ReceivedPacket>();
                    Assert.IsTrue(WaitUntil(() =>
                    {
                        while (connection.TryDequeue(out var packet))
                            received.Add(packet);
                        return received.Count >= 2;
                    }), "패킷 2개를 시간 안에 받지 못함");

                    Assert.AreEqual(10003, received[0].PacketID);
                    Assert.AreEqual(60001, received[1].PacketID);
                    CollectionAssert.AreEqual(new byte[] { 2, 3 }, received[1].Payload);
                }

                // 서버 쪽 소켓을 닫으면 수신 스레드가 끊김을 감지해야 한다.
                Assert.IsTrue(WaitUntil(() => !connection.IsConnected), "서버 종료를 감지하지 못함");
            }
            finally
            {
                connection.Close();
                listener.Stop();
            }
        }

        [Test]
        public void Send_WritesHeaderAndPayload()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var connection = new TcpConnection();
            try
            {
                connection.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                using (TcpClient server = listener.AcceptTcpClient())
                {
                    Assert.IsTrue(connection.Send(102, new byte[] { 9, 8, 7 }));

                    // 서버 입장에서 헤더 8바이트 + payload 3바이트가 그대로 와야 한다.
                    server.ReceiveTimeout = TimeoutMs;
                    var bytes = new byte[PacketHeader.HeaderSize + 3];
                    int read = 0;
                    while (read < bytes.Length)
                        read += server.GetStream().Read(bytes, read, bytes.Length - read);

                    Assert.IsTrue(PacketHeader.TryRead(bytes, out int length, out int packetID));
                    Assert.AreEqual(bytes.Length, length);
                    Assert.AreEqual(102, packetID);
                    CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, new ArraySegment<byte>(bytes, PacketHeader.HeaderSize, 3));
                }
            }
            finally
            {
                connection.Close();
                listener.Stop();
            }
        }

        private static bool WaitUntil(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < TimeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(10);
            }
            return condition();
        }
    }
}
