using System.Net.Sockets;
using Google.Protobuf;
using Th;
using TH.Common.Network;

namespace TH.DummyClient;

// 로그인 흐름(CALoginReq → ACLoginAck) E2E 검증용 순수 TCP 더미 클라이언트.
// 서버에 접속해 CALoginReq 를 보내고 ACLoginAck 응답을 파싱·출력한다.
// 프레이밍은 서버와 동일한 PacketHeader(8바이트: length LE + packetID LE)를 그대로 재사용한다.
internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 35501;
    private const int RecvTimeoutMs = 5000;

    private static int Main(string[] args)
    {
        // args[0] = "host:port" (선택). 없으면 기본값.
        string host = DefaultHost;
        int port = DefaultPort;
        if (args.Length > 0)
        {
            var parts = args[0].Split(':');
            host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int p))
                port = p;
        }

        try
        {
            using var client = new TcpClient();
            Console.WriteLine($"[dummy] connecting to {host}:{port} ...");
            client.Connect(host, port);
            client.NoDelay = true;
            var stream = client.GetStream();
            stream.ReadTimeout = RecvTimeoutMs;

            // 1) CALoginReq 송신 — required 필드 전부 채움 (AuthToken 은 서버가 검증하지 않음).
            var req = new CALoginReq
            {
                MessageID      = EMessageID.CaLoginReq,
                CurrentVersion = 1,
                PID            = "dummy_pid",
                AuthToken      = "dummy_token",
                PlatformType   = EPlatformType.None,
                LanguageID     = 1,
                IsReconnect    = false,
            };
            SendPacket(stream, (int)EMessageID.CaLoginReq, req);
            Console.WriteLine($"[dummy] sent CALoginReq (PID={req.PID})");

            // 2) 응답 수신 — 헤더 1개 + payload 1개.
            var (packetID, payload) = ReceivePacket(stream);
            if (packetID != (int)EMessageID.AcLoginAck)
            {
                Console.Error.WriteLine($"[dummy] unexpected packetID={packetID} (expected AcLoginAck={(int)EMessageID.AcLoginAck})");
                return 1;
            }

            var ack = ACLoginAck.Parser.ParseFrom(payload);
            Console.WriteLine("[dummy] received ACLoginAck:");
            Console.WriteLine($"  AccountID               = {ack.AccountID}");
            Console.WriteLine($"  AccountName             = '{ack.AccountName}'");
            Console.WriteLine($"  ConntectedIP            = '{ack.ConntectedIP}'");
            Console.WriteLine($"  ConnectedPort           = {ack.ConnectedPort}");
            Console.WriteLine($"  IsReconnect             = {ack.IsReconnect}");
            Console.WriteLine($"  IsNewAccount            = {ack.IsNewAccount}");
            Console.WriteLine($"  FreeNicknameChangeCount = {ack.FreeNicknameChangeCount}");
            Console.WriteLine($"  Version                 = '{ack.Version}'");
            Console.WriteLine($"  ServerID                = {ack.ServerID}");
            Console.WriteLine($"  ChannelID               = {ack.ChannelID}");
            Console.WriteLine("[dummy] OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dummy] FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    // payload = protobuf 직렬화 결과. [length LE][packetID LE][payload] 한 덩어리로 송신.
    private static void SendPacket(NetworkStream stream, int packetID, IMessage msg)
    {
        byte[] payload = msg.ToByteArray();
        int total = PacketHeader.HeaderSize + payload.Length;
        byte[] buffer = new byte[total];
        PacketHeader.Write(buffer, total, packetID);
        payload.CopyTo(buffer.AsSpan(PacketHeader.HeaderSize));
        stream.Write(buffer, 0, total);
        stream.Flush();
    }

    // 8바이트 헤더를 먼저 읽어 총길이를 알아낸 뒤, 나머지 payload 를 모두 채워 반환.
    private static (int packetID, byte[] payload) ReceivePacket(NetworkStream stream)
    {
        byte[] header = ReadExact(stream, PacketHeader.HeaderSize);
        if (!PacketHeader.TryRead(header, out int length, out int packetID))
            throw new InvalidOperationException("header read failed");
        if (length < PacketHeader.HeaderSize)
            throw new InvalidOperationException($"invalid packet length {length}");

        int payloadLength = length - PacketHeader.HeaderSize;
        byte[] payload = payloadLength > 0 ? ReadExact(stream, payloadLength) : Array.Empty<byte>();
        return (packetID, payload);
    }

    // count 바이트를 모두 읽을 때까지 블로킹. 연결 종료(0바이트) 시 예외.
    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new IOException("connection closed by server");
            offset += read;
        }
        return buffer;
    }
}
