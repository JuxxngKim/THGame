namespace TH.Server;

internal static class Program
{
    private static int Main(string[] args)
    {
        var app = new GameServerApp();
        if (!app.Start())
            return 1;
        app.Run();
        return 0;
    }
}
