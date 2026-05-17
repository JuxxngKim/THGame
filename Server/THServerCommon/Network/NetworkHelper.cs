using System.Net;

namespace TH.Common.Network;

public static class NetworkHelper
{
    public static bool TryParseEndPoint(string addr, out IPEndPoint? endPoint)
    {
        endPoint = null;
        if (string.IsNullOrWhiteSpace(addr))
            return false;

        var colon = addr.LastIndexOf(':');
        if (colon <= 0 || colon == addr.Length - 1)
            return false;

        var host = addr[..colon];
        var portStr = addr[(colon + 1)..];

        if (!IPAddress.TryParse(host, out var ip))
            return false;
        if (!ushort.TryParse(portStr, out var port))
            return false;

        endPoint = new IPEndPoint(ip, port);
        return true;
    }
}
