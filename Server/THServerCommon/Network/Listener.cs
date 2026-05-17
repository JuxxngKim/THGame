using System.Net;
using System.Net.Sockets;
using Serilog;

namespace TH.Common.Network;

public sealed class Listener
{
    private readonly Action<Session> _onSessionCreated;
    private readonly SocketAsyncEventArgs _acceptSaea;

    private Socket? _listenSocket;
    private volatile bool _stopped;

    public Listener(Action<Session> onSessionCreated)
    {
        _onSessionCreated = onSessionCreated;
        _acceptSaea = new SocketAsyncEventArgs();
        _acceptSaea.Completed += OnAcceptCompleted;
    }

    public void Start(IPEndPoint endPoint, int backlog = 100)
    {
        _listenSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.Bind(endPoint);
        _listenSocket.Listen(backlog);

        AcceptLoop();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            if (_stopped) return;

            var listenSocket = _listenSocket;
            if (listenSocket is null) return;

            _acceptSaea.AcceptSocket = null;

            bool pending;
            try
            {
                pending = listenSocket.AcceptAsync(_acceptSaea);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AcceptAsync 호출 실패");
                return;
            }

            if (pending) return;
            ProcessAcceptResult();
        }
    }

    private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
    {
        ProcessAcceptResult();
        AcceptLoop();
    }

    private void ProcessAcceptResult()
    {
        if (_stopped) return;

        if (_acceptSaea.SocketError != SocketError.Success)
        {
            if (!_stopped)
                Log.Warning("Accept 실패: {Error}", _acceptSaea.SocketError);
            return;
        }

        var acceptSocket = _acceptSaea.AcceptSocket;
        if (acceptSocket is null) return;

        Session? session = null;
        try
        {
            acceptSocket.NoDelay = true;
            session = new Session(acceptSocket);
            _onSessionCreated(session);
            session.BeginReceive();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "세션 생성/등록 실패");
            if (session is not null)
            {
                session.Close(notify: true);
            }
            else
            {
                try { acceptSocket.Close(); } catch { }
            }
        }
    }

    public void Stop()
    {
        _stopped = true;

        var socket = _listenSocket;
        _listenSocket = null;
        if (socket is not null)
        {
            try { socket.Close(); } catch { }
        }

        // _acceptSaea Dispose 생략 (Session과 동일 race 이유, 일관성)
    }
}
