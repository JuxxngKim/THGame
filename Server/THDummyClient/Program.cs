using System.Net.Sockets;
using Google.Protobuf;
using Th;
using TH.Common.Network;

namespace TH.DummyClient;

// 다중 세션 E2E 테스트용 TCP 더미 클라이언트.
// N개 세션이 각자 COLoginReq → OCLoginAck → COEnterReq → ICEnterNoti 순서를 거친 뒤 holdMs 동안 유지하고 소켓을 닫는다.
// 소켓을 닫으면 서버의 세션 종료 정리(NetDisconnect 경로)까지 확인할 수 있다.
// 프레이밍은 서버의 PacketHeader(8바이트: length LE + packetID LE)를 그대로 쓴다.
internal static class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 35501;
    private const int DefaultCount = 1;
    private const int DefaultHoldMs = 3000;
    private const int RecvTimeoutMs = 5000;
    private const int EnterStageID = 1;

    // 세션이 멈춘 단계. 실패 분류용.
    private enum Stage { Connect, Login, Enter, Done }

    private sealed record SessionResult(int Index, bool Success, Stage FailedAt, string? Error);

    private static async Task<int> Main(string[] args)
    {
        // 위치 인자: [count] [host:port] [holdMs]. 첫 인자가 정수면 count로 본다.
        int count = DefaultCount;
        string host = DefaultHost;
        int port = DefaultPort;
        int holdMs = DefaultHoldMs;

        int argIndex = 0;

        // arg0: count(정수일 때만). 정수가 아니면 count가 생략된 것으로 보고 host:port 파싱으로 넘어간다.
        if (argIndex < args.Length && int.TryParse(args[argIndex], out int parsedCount))
        {
            if (parsedCount > 0)
                count = parsedCount;
            else
                Console.Error.WriteLine($"[dummy] invalid count '{args[argIndex]}', fallback to {count}");
            argIndex++;
        }

        // arg: host:port (콜론 포함 토큰).
        if (argIndex < args.Length && args[argIndex].Contains(':'))
        {
            var parts = args[argIndex].Split(':');
            host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int p))
                port = p;
            argIndex++;
        }

        // arg: holdMs.
        if (argIndex < args.Length && int.TryParse(args[argIndex], out int parsedHold))
        {
            if (parsedHold >= 0)
                holdMs = parsedHold;
            else
                Console.Error.WriteLine($"[dummy] invalid holdMs '{args[argIndex]}', fallback to {holdMs}");
            argIndex++;
        }

        Console.WriteLine($"[dummy] starting {count} session(s) -> {host}:{port}, holdMs={holdMs}");

        var startedAt = DateTime.UtcNow;

        // N개 세션을 동시에 띄운다. async IO라 세션마다 스레드를 잡지 않는다.
        var tasks = new Task<SessionResult>[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            tasks[i] = RunSessionAsync(index, host, port, holdMs);
        }

        SessionResult[] results = await Task.WhenAll(tasks);

        double elapsedSec = (DateTime.UtcNow - startedAt).TotalSeconds;

        int success = results.Count(r => r.Success);
        int failed = count - success;

        Console.WriteLine("[dummy] ======== summary ========");
        Console.WriteLine($"[dummy] total={count} success={success} failed={failed} elapsed={elapsedSec:F2}s");

        if (failed > 0)
        {
            // 실패한 세션을 멈춘 단계별로 묶어 출력.
            foreach (var grp in results.Where(r => !r.Success)
                                       .GroupBy(r => r.FailedAt)
                                       .OrderBy(g => g.Key))
            {
                Console.WriteLine($"[dummy]   failed@{grp.Key}: {grp.Count()} " +
                                  $"(e.g. s{grp.First().Index}: {grp.First().Error})");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    // 한 세션의 전체 순서. 실패하면 멈춘 단계를 SessionResult에 담아 반환한다(throw하지 않음).
    private static async Task<SessionResult> RunSessionAsync(int index, string host, int port, int holdMs)
    {
        var stage = Stage.Connect;
        string pid = $"dummy_pid_{index}";

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port);
            client.NoDelay = true;
            var stream = client.GetStream();

            // 1) 로그인. required 필드를 모두 채운다(AuthToken은 서버가 검증하지 않음).
            stage = Stage.Login;
            var loginReq = new COLoginReq
            {
                MessageID      = EMessageID.CoLoginReq,
                CurrentVersion = 1,
                PID            = pid,
                AuthToken      = "dummy_token",
                LanguageID     = 1,
                IsReconnect    = false,
            };
            await SendPacketAsync(stream, (int)EMessageID.CoLoginReq, loginReq);

            var (loginPacketID, loginPayload) = await ReceivePacketAsync(stream);
            if (loginPacketID != (int)EMessageID.OcLoginAck)
                return Fail(index, stage, $"unexpected packetID={loginPacketID} (expected OcLoginAck)");

            var ack = OCLoginAck.Parser.ParseFrom(loginPayload);
            Console.WriteLine($"[s{index}] login ok AccountID={ack.AccountID} Name='{ack.AccountName}'");

            // 2) 필드 진입. COEnterReq → ICEnterNoti.
            stage = Stage.Enter;
            var enterReq = new COEnterReq
            {
                MessageID = EMessageID.CoEnterReq,
                StageID   = EnterStageID,
            };
            await SendPacketAsync(stream, (int)EMessageID.CoEnterReq, enterReq);

            var (enterPacketID, enterPayload) = await ReceivePacketAsync(stream);
            if (enterPacketID != (int)EMessageID.IcEnterNoti)
                return Fail(index, stage, $"unexpected packetID={enterPacketID} (expected IcEnterNoti)");

            var noti = ICEnterNoti.Parser.ParseFrom(enterPayload);
            Console.WriteLine($"[s{index}] enter ok pos=({noti.Position.X},{noti.Position.Y},{noti.Position.Z})");

            // 3) 진입 후 holdMs 동안 연결을 유지한다. 동시 세션이 머무는 상황을 재현한다.
            stage = Stage.Done;
            if (holdMs > 0)
                await Task.Delay(holdMs);

            // 4) using dispose로 소켓이 닫히면 서버의 NetDisconnect 정리가 시작된다.
            Console.WriteLine($"[s{index}] closing");
            return new SessionResult(index, true, Stage.Done, null);
        }
        catch (Exception ex)
        {
            return Fail(index, stage, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static SessionResult Fail(int index, Stage stage, string error)
    {
        Console.Error.WriteLine($"[s{index}] FAILED@{stage}: {error}");
        return new SessionResult(index, false, stage, error);
    }

    // payload는 protobuf 직렬화 결과. [length LE][packetID LE][payload]를 한 덩어리로 보낸다.
    private static async Task SendPacketAsync(NetworkStream stream, int packetID, IMessage msg)
    {
        byte[] payload = msg.ToByteArray();
        int total = PacketHeader.HeaderSize + payload.Length;
        byte[] buffer = new byte[total];
        PacketHeader.Write(buffer, total, packetID);
        payload.CopyTo(buffer.AsSpan(PacketHeader.HeaderSize));
        await stream.WriteAsync(buffer.AsMemory(0, total));
        await stream.FlushAsync();
    }

    // 8바이트 헤더를 먼저 읽어 전체 길이를 알아낸 뒤, 나머지 payload를 모두 읽어 반환한다.
    private static async Task<(int packetID, byte[] payload)> ReceivePacketAsync(NetworkStream stream)
    {
        byte[] header = await ReadExactAsync(stream, PacketHeader.HeaderSize);
        if (!PacketHeader.TryRead(header, out int length, out int packetID))
            throw new InvalidOperationException("header read failed");
        if (length < PacketHeader.HeaderSize)
            throw new InvalidOperationException($"invalid packet length {length}");

        int payloadLength = length - PacketHeader.HeaderSize;
        byte[] payload = payloadLength > 0 ? await ReadExactAsync(stream, payloadLength) : Array.Empty<byte>();
        return (packetID, payload);
    }

    // count 바이트를 모두 읽을 때까지 비동기로 기다린다. 연결이 끊기면(0바이트) 예외.
    // 동기 ReadTimeout은 async에 적용되지 않으므로 CancellationToken으로 수신 타임아웃을 건다.
    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            using var cts = new CancellationTokenSource(RecvTimeoutMs);
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cts.Token);
            if (read == 0)
                throw new IOException("connection closed by server");
            offset += read;
        }
        return buffer;
    }
}
