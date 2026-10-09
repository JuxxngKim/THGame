using System;
using Google.Protobuf;
using NUnit.Framework;
using TH.Network;
using Th;

namespace TH.Tests
{
    public class PacketDispatcherTests
    {
        [Test]
        public void RegisteredPacket_InvokesHandlerWithParsedMessage()
        {
            var dispatcher = new PacketDispatcher();
            NetAliveAck received = null;
            dispatcher.Register<NetAliveAck>((int)EMessageID.NetAliveAck, ack => received = ack);

            var payload = new NetAliveAck { ResponseMS = 42 }.ToByteArray();
            dispatcher.Dispatch(new ReceivedPacket((int)EMessageID.NetAliveAck, payload));

            Assert.IsNotNull(received);
            Assert.AreEqual(42, received.ResponseMS);
        }

        [Test]
        public void UnregisteredPacket_CallsOnUnhandled()
        {
            var dispatcher = new PacketDispatcher();
            int unhandledID = 0;
            dispatcher.OnUnhandled = packet => unhandledID = packet.PacketID;

            dispatcher.Dispatch(new ReceivedPacket(60001, Array.Empty<byte>()));

            Assert.AreEqual(60001, unhandledID);
        }

        [Test]
        public void CorruptPayload_ReportsOnError_AndNextPacketStillWorks()
        {
            var dispatcher = new PacketDispatcher();
            int handled = 0;
            Exception error = null;
            dispatcher.Register<NetAliveAck>((int)EMessageID.NetAliveAck, ack => handled++);
            dispatcher.OnError = (packet, ex) => error = ex;

            // 길이가 맞지 않는 varint 라서 파싱이 실패한다.
            dispatcher.Dispatch(new ReceivedPacket((int)EMessageID.NetAliveAck, new byte[] { 0xFF, 0xFF, 0xFF }));
            Assert.IsInstanceOf<InvalidProtocolBufferException>(error);
            Assert.AreEqual(0, handled);

            dispatcher.Dispatch(new ReceivedPacket((int)EMessageID.NetAliveAck, new NetAliveAck().ToByteArray()));
            Assert.AreEqual(1, handled);
        }
    }
}
