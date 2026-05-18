using System.Collections.Concurrent;
using System.Net;
using Serilog;

namespace TH.Common.Network;

public sealed class NetworkManager : Singleton<NetworkManager>
{
    private Listener? _listener;
    private readonly ConcurrentDictionary<long, Session> _sessions = new();

    public event Action<Session>? OnSessionConnected;
    public event Action<Session>? OnSessionDisconnected;

    private NetworkManager() { }

    public bool Init(IPEndPoint endPoint)
    {
        if (_listener is not null)
        {
            Log.Warning("NetworkManager already initialized");
            return true;
        }

        try
        {
            _listener = new Listener(OnNewSession);
            _listener.Start(endPoint);

            Log.Information("Listener bound on {EndPoint}", endPoint);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "NetworkManager init failed (endPoint={EndPoint})", endPoint);
            return false;
        }
    }

    public void Shutdown()
    {
        _listener?.Stop();
        _listener = null;

        foreach (var s in _sessions.Values)
            s.Close(notify: true);

        _sessions.Clear();

        Log.Information("NetworkManager shutdown");
    }

    public Session? FindSession(long sessionId)
    {
        _sessions.TryGetValue(sessionId, out var s);
        return s;
    }

    public bool IsSessionAlive(long sessionId) => _sessions.ContainsKey(sessionId);

    public void CloseSession(long sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
            s.Close(notify: true);
        // OnSessionDisconnected는 직접 발화하지 않음.
        // Session.OnDisconnected → OnSessionDisconnectedInternal 경로로 자연 발화.
    }

    private void OnNewSession(Session session)
    {
        _sessions.TryAdd(session.SessionId, session);
        session.OnDisconnected += OnSessionDisconnectedInternal;
        OnSessionConnected?.Invoke(session);
    }

    private void OnSessionDisconnectedInternal(Session session)
    {
        if (_sessions.TryRemove(session.SessionId, out _))
            OnSessionDisconnected?.Invoke(session);
    }
}
