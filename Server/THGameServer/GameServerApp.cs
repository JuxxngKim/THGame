using System.Reflection;
using System.Runtime.CompilerServices;
using Serilog;
using Th;
using TH.Common.Config;
using TH.Common.Network;
using TH.Common.Time;
using TH.Server.Common;
using TH.Server.Data;
using TH.Server.Logging;
using TH.Server.Logic;

namespace TH.Server;

public sealed class GameServerApp
{
    private volatile bool _shutdown;
    private volatile bool _exited;

    public bool Start()
    {
        try
        {
            _ = TimeManager.Instance;

            var configDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "config"));
            if (!ConfigManager.Instance.Init(configDir))
                return false;

            LoggerSetup.Init(TimeManager.Instance);

            Log.Information("GameServer started (Env={Env}, ID={ID})",
                ConfigManager.Instance.Env, ConfigManager.Instance.ID);

            InitPacketHandlers();

            // OutGameService 초기화 시점에 OutGameLogicEventor가 만들어지며 핸들러가 모두 등록된다.
            OutGameService.Instance.Init();

            // InGameService: 룸(필드) 시뮬레이션. OutGameService와 독립된 자체 100ms tick 스레드.
            InGameService.Instance.Init();

            // Data(DB) 계층: OD* 요청을 받아 DO*로 응답한다. worker(샤드) 스레드를 띄운다.
            DBService.Instance.Init();

            var section  = $"Game.{ConfigManager.Instance.ID}";
            var bindAddr = ConfigManager.Instance.GetRequired(section, "BindAddr");
            if (!NetworkHelper.TryParseEndPoint(bindAddr, out var endPoint) || endPoint is null)
            {
                Log.Error("BindAddr parse failed: {Addr}", bindAddr);
                return false;
            }

            if (!NetworkManager.Instance.Init(endPoint))
                return false;

            // Listener.ProcessAcceptResult는 OnSessionConnected를 호출한 뒤 BeginReceive를 부르므로
            // OnPacketReceived를 설정하는 시점에는 아직 수신이 시작되지 않았다(race 없음).
            NetworkManager.Instance.OnSessionConnected += session =>
            {
                session.OnPacketReceived = (s, packetID, payload) =>
                {
                    // 클라가 보낼 수 있는 패킷만 대역별로 나눠 받고 나머지는 버린다(허용 목록).
                    // NetDisconnect나 OD/DO·OI/IO 같은 서버 내부 패킷을 클라가 보내면 로그인·상태 검사를 건너뛸 수 있기 때문이다.
                    // payload는 수신 버퍼의 ReadOnlySpan<byte> 슬라이스라 즉시 ToArray로 복사한다(Span 캡처 금지).
                    if (PacketBand.IsClientToOutGame(packetID))
                        OutGameService.Instance.EnqueuePacket(s.SessionID, packetID, payload.ToArray());
                    else if (PacketBand.IsClientToInGame(packetID))
                        InGameService.Instance.EnqueuePacket(s.SessionID, packetID, payload.ToArray());
                    else
                        Log.Warning("Rejected non-client packet SessionID={ID} PacketID={PID}", s.SessionID, packetID);
                };
            };

            // 세션 종료를 NetDisconnect 패킷으로 만들어 OutGame PacketQueue에 넣는다.
            // 이후 흐름: Player(Work)가 DB 세션 종료 저장(ODExitGameSessionReq)을 시작하고,
            // 완료(DOExitGameSessionAck)를 받은 Arrange에서 archive 제거와 InGame 캐릭터 정리(OIExitGameSessionReq)까지 처리한다.
            // InGame 캐릭터 제거는 이 ExitGameSession 경로 하나로 통일했으므로 여기서 OILeaveReq를 따로 넣지 않는다.
            NetworkManager.Instance.OnSessionDisconnected += session =>
            {
                OutGameService.Instance.EnqueuePacket(
                    session.SessionID, (int)EMessageID.NetDisconnect, Array.Empty<byte>());
            };

            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "GameServerApp.Start failed");
            return false;
        }
    }

    public void Run()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _shutdown = true;
        };

        Log.Debug("Main loop running");

        while (!_shutdown)
        {
            Thread.Sleep(1);
        }

        Exit();
    }

    public void Exit()
    {
        if (_exited)
            return;
        _exited = true;

        Log.Information("GameServer shutdown");

        NetworkManager.Instance.Shutdown();
        OutGameService.Instance.Shutdown();
        InGameService.Instance.Shutdown();
        DBService.Instance.Shutdown();
        ConfigManager.Instance.Shutdown();
        Log.CloseAndFlush();
    }

    // 패킷 핸들러 표(static 생성자)를 지금 만든다. 그냥 두면 첫 로그인·입장 때 만들어진다.
    // 그때 등록 실수(MessageID default 누락·복붙·중복)로 예외가 나면 TypeInitializationException 이 영구히 남아
    // 그 뒤 모든 로그인이 멈추는데, 서버는 겉보기에 정상이다. 여기서 터지면 Start 가 실패해 바로 드러난다.
    // 대상은 static PacketDispatcher<> 필드를 가진 타입을 reflection 으로 찾는다. 목록을 손으로 적으면 새 타입을 빠뜨릴 수 있다.
    private static void InitPacketHandlers()
    {
        var initialized = new List<string>();
        foreach (var type in typeof(GameServerApp).Assembly.GetTypes())
        {
            // 열린 generic 타입(PacketDispatcher<TOwner> 자신 등)은 static 생성자를 돌릴 수 없다.
            if (type.IsGenericTypeDefinition)
                continue;

            bool hasDispatcher = type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(PacketDispatcher<>));
            if (!hasDispatcher)
                continue;

            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            initialized.Add(type.Name);
        }

        Log.Information("Packet handlers initialized: {Types}", string.Join(", ", initialized));
    }
}
