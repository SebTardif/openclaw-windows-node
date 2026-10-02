using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Connection;

/// <summary>
/// Holds a loopback forward port after SSH teardown so a different listener
/// cannot accept a dashboard URL that was already handed to the browser.
/// The next SSH start releases the port immediately before it binds.
/// </summary>
internal static class DashboardForwardPortGuard
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, TcpListener> Listeners = new();

    internal static void Hold(int port)
    {
        if (port is < 1 or > 65535)
            return;

        lock (Gate)
        {
            if (Listeners.ContainsKey(port))
                return;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Listeners[port] = listener;
            }
            catch (SocketException)
            {
            }
        }
    }

    internal static void Release(int port)
    {
        lock (Gate)
        {
            if (!Listeners.Remove(port, out var listener))
                return;

            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
            }
        }
    }

    internal static bool IsHolding(int port)
    {
        lock (Gate)
            return Listeners.ContainsKey(port);
    }
}
