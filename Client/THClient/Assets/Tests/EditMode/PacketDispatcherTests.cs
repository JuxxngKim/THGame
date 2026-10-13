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
            // packetID 를 따로 넘기지 않는다. NetAliveAck 의 MessageID default(NET_ALIVE_ACK)로 등록돼야 한다.
            dispatcher.Register<NetAliveAck>(ack => received = ack);

            var payload = new NetAliveAck { ResponseMS = 42 }.ToByteArray();
            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, payload));

            Assert.IsNotNull(received);
            Assert.AreEqual(42, received.ResponseMS);
        }

        [Test]
        public void UnregisteredPacket_CallsOnUnhandled()
        {
            var dispatcher = new PacketDispatcher();
            int unhandledID = 0;
            dispatcher.OnUnhandled = packet => unhandledID = packet.PacketID;

            dispatcher.Dispatch(new PacketMessage(60001, Array.Empty<byte>()));

            Assert.AreEqual(60001, unhandledID);
        }

        [Test]
        public void CorruptPayload_ReportsOnError_AndNextPacketStillWorks()
        {
            var dispatcher = new PacketDispatcher();
            int handled = 0;
            Exception error = null;
            dispatcher.Register<NetAliveAck>(ack => handled++);
            dispatcher.OnError = (packet, ex) => error = ex;

            // 길이가 맞지 않는 varint 라서 파싱이 실패한다.
            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, new byte[] { 0xFF, 0xFF, 0xFF }));
            Assert.IsInstanceOf<InvalidProtocolBufferException>(error);
            Assert.AreEqual(0, handled);

            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, new NetAliveAck().ToByteArray()));
            Assert.AreEqual(1, handled);
        }

        [Test]
        public void Register_MessageWithoutMessageID_Throws()
        {
            // MPosition 은 다른 메시지 안에 들어가는 부품이라 MessageID 가 없다. 등록할 때 바로 알려야 한다.
            var dispatcher = new PacketDispatcher();
            Assert.Throws<InvalidOperationException>(() => dispatcher.Register<MPosition>(msg => { }));
        }

        [Test]
        public void Register_SamePacketTwice_Throws()
        {
            // 덮어쓰면 먼저 등록한 핸들러가 조용히 사라지므로 예외로 알려야 한다.
            var dispatcher = new PacketDispatcher();
            dispatcher.Register<NetAliveAck>(ack => { });
            Assert.Throws<InvalidOperationException>(() => dispatcher.Register<NetAliveAck>(ack => { }));
        }

        [Test]
        public void Unregister_ThenPacketIsUnhandled_AndCanRegisterAgain()
        {
            // 씬 재진입처럼 해제 후 다시 등록하는 흐름.
            var dispatcher = new PacketDispatcher();
            int unhandledID = 0;
            dispatcher.OnUnhandled = packet => unhandledID = packet.PacketID;
            Action<NetAliveAck> first = ack => { };
            dispatcher.Register(first);

            Assert.IsTrue(dispatcher.Unregister(first));
            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, new NetAliveAck().ToByteArray()));
            Assert.AreEqual((int)EMessageID.NetAliveAck, unhandledID);

            int handled = 0;
            dispatcher.Register<NetAliveAck>(ack => handled++);
            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, new NetAliveAck().ToByteArray()));
            Assert.AreEqual(1, handled);
        }

        [Test]
        public void Unregister_OtherHandler_KeepsRegisteredOne()
        {
            // Register 가 중복으로 실패한 쪽이 OnDisable 에서 Unregister 해도, 먼저 등록한 핸들러는 남아야 한다.
            var dispatcher = new PacketDispatcher();
            int handled = 0;
            dispatcher.Register<NetAliveAck>(ack => handled++);

            Action<NetAliveAck> other = ack => { };
            Assert.IsFalse(dispatcher.Unregister(other));

            dispatcher.Dispatch(new PacketMessage((int)EMessageID.NetAliveAck, new NetAliveAck().ToByteArray()));
            Assert.AreEqual(1, handled);
        }
    }
}
