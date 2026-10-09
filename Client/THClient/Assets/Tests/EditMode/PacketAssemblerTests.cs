using System;
using System.Collections.Generic;
using NUnit.Framework;
using TH.Network;

namespace TH.Tests
{
    // TCP 스트림을 패킷으로 자르는 규칙 검증. 소켓 없이 바이트 조각을 직접 넣는다.
    public class PacketAssemblerTests
    {
        [Test]
        public void SplitAcrossChunks_ProducesOnePacket()
        {
            byte[] packet = MakePacket(10002, new byte[] { 1, 2, 3, 4, 5 });
            var assembler = new PacketAssembler();
            var output = new List<ReceivedPacket>();

            // 헤더 중간(3바이트), 헤더 나머지 + payload 일부, 나머지 순서로 나눠 넣는다.
            Assert.IsTrue(assembler.Append(packet, 0, 3, output.Add));
            Assert.AreEqual(0, output.Count);
            Assert.IsTrue(assembler.Append(packet, 3, 7, output.Add));
            Assert.AreEqual(0, output.Count);
            Assert.IsTrue(assembler.Append(packet, 10, packet.Length - 10, output.Add));

            Assert.AreEqual(1, output.Count);
            Assert.AreEqual(10002, output[0].PacketID);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, output[0].Payload);
        }

        [Test]
        public void TwoPacketsInOneChunk_ProducesBothInOrder()
        {
            byte[] first = MakePacket(102, new byte[] { 7, 8 });
            byte[] second = MakePacket(103, Array.Empty<byte>()); // payload 없는 패킷(예: NetAliveAck)
            byte[] joined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, joined, 0, first.Length);
            Buffer.BlockCopy(second, 0, joined, first.Length, second.Length);

            var assembler = new PacketAssembler();
            var output = new List<ReceivedPacket>();
            Assert.IsTrue(assembler.Append(joined, 0, joined.Length, output.Add));

            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(102, output[0].PacketID);
            CollectionAssert.AreEqual(new byte[] { 7, 8 }, output[0].Payload);
            Assert.AreEqual(103, output[1].PacketID);
            Assert.AreEqual(0, output[1].Payload.Length);
        }

        [Test]
        public void CompletePacketPlusPartialNext_KeepsRemainderForNextChunk()
        {
            byte[] first = MakePacket(10003, new byte[] { 1, 2 });
            byte[] second = MakePacket(60001, new byte[] { 3, 4, 5, 6 });
            const int splitAt = 5; // 두 번째 패킷의 헤더 일부만 첫 조각에 싣는다.

            byte[] chunk1 = new byte[first.Length + splitAt];
            Buffer.BlockCopy(first, 0, chunk1, 0, first.Length);
            Buffer.BlockCopy(second, 0, chunk1, first.Length, splitAt);

            var assembler = new PacketAssembler();
            var output = new List<ReceivedPacket>();
            Assert.IsTrue(assembler.Append(chunk1, 0, chunk1.Length, output.Add));
            Assert.AreEqual(1, output.Count);

            // 남은 조각이 버퍼 앞으로 제대로 당겨졌다면, 나머지를 붙였을 때 두 번째 패킷이 온전히 나온다.
            Assert.IsTrue(assembler.Append(second, splitAt, second.Length - splitAt, output.Add));
            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(60001, output[1].PacketID);
            CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6 }, output[1].Payload);
        }

        [TestCase(PacketHeader.HeaderSize - 1)]
        [TestCase(PacketAssembler.MaxPacketSize + 1)]
        public void InvalidLength_ReturnsFalse(int length)
        {
            var header = new byte[PacketHeader.HeaderSize];
            PacketHeader.Write(header, length, 1);

            var output = new List<ReceivedPacket>();
            Assert.IsFalse(new PacketAssembler().Append(header, 0, header.Length, output.Add));
            Assert.AreEqual(0, output.Count);
        }

        internal static byte[] MakePacket(int packetID, byte[] payload)
        {
            var bytes = new byte[PacketHeader.HeaderSize + payload.Length];
            PacketHeader.Write(bytes, bytes.Length, packetID);
            Buffer.BlockCopy(payload, 0, bytes, PacketHeader.HeaderSize, payload.Length);
            return bytes;
        }
    }
}
