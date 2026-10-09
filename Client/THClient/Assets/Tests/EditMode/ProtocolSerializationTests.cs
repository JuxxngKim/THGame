using Google.Protobuf;
using NUnit.Framework;
using Th;

namespace TH.Tests
{
    // 클라 쪽 protobuf 구성(생성 코드 + Google.Protobuf + Unsafe)으로 직렬화와 파싱이 되는지 확인한다.
    // 서버와 실제로 바이트를 주고받는 검증은 TCP 접속이 생기는 로드맵 세션 4 이후에 한다.
    public class ProtocolSerializationTests
    {
        [Test]
        public void COLoginReq_RoundTrip_KeepsAllFields()
        {
            var original = new COLoginReq
            {
                MessageID      = EMessageID.CoLoginReq,
                CurrentVersion = 1,
                PID            = "test_pid",
                AuthToken      = "test_token",
                LanguageID     = 1,
                IsReconnect    = false,
            };

            byte[] bytes = original.ToByteArray();
            var parsed = COLoginReq.Parser.ParseFrom(bytes);

            // protobuf 메시지의 Equals 는 모든 필드 값과 설정 여부까지 비교한다.
            Assert.That(bytes.Length, Is.GreaterThan(0));
            Assert.AreEqual(original, parsed);
        }
    }
}
