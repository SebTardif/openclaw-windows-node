using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Connection;

/// <summary>
/// Owns a loopback forward port after SSH teardown so a different process
/// cannot accept a dashboard URL that was already handed to the browser.
/// The listener stays up while a replacement SSH process binds a backend port.
/// </summary>
internal static class DashboardForwardPortGuard
{
    private sealed class Slot
    {
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Cancel { get; init; }
        public int? BackendPort { get; set; }
        public int? BackendProcessId { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Slot> Listeners = new();

    internal static void Hold(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        lock (Gate)
        {
            if (Listeners.ContainsKey(port))
                return;

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            var cancel = new CancellationTokenSource();
            var slot = new Slot { Listener = listener, Cancel = cancel };
            Listeners[port] = slot;
            _ = Task.Run(() => AcceptAsync(port, slot));
        }
    }

    internal static void SetBackend(int port, int backendPort, int backendProcessId)
    {
        lock (Gate)
        {
            if (Listeners.TryGetValue(port, out var slot))
            {
                slot.BackendPort = backendPort;
                slot.BackendProcessId = backendProcessId;
            }
        }
    }

    internal static void Release(int port)
    {
        Slot? slot;
        lock (Gate)
        {
            if (!Listeners.Remove(port, out slot))
                return;
        }

        slot.Cancel.Cancel();
        try
        {
            slot.Listener.Stop();
        }
        catch (SocketException)
        {
        }
    }

    internal static bool IsHolding(int port)
    {
        lock (Gate)
            return Listeners.ContainsKey(port);
    }

    internal static int? BackendPort(int port)
    {
        lock (Gate)
            return Listeners.TryGetValue(port, out var slot) ? slot.BackendPort : null;
    }

    private static async Task AcceptAsync(int port, Slot slot)
    {
        try
        {
            while (!slot.Cancel.IsCancellationRequested)
            {
                var client = await slot.Listener.AcceptTcpClientAsync(slot.Cancel.Token).ConfigureAwait(false);
                _ = Task.Run(() => Pump(port, client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Pump(int port, TcpClient client)
    {
        using (client)
        {
            int? backendPort;
            int? backendProcessId;
            lock (Gate)
            {
                if (!Listeners.TryGetValue(port, out var slot))
                    return;
                backendPort = slot.BackendPort;
                backendProcessId = slot.BackendProcessId;
            }

            if (backendPort is not int target || backendProcessId is not int processId || processId <= 0)
                return;
            if (!BackendIsOwnedBy(target, processId))
                return;

            try
            {
                using var backend = new TcpClient();
                backend.Connect(IPAddress.Loopback, target);
                var left = client.GetStream();
                var right = backend.GetStream();
                var toRight = left.CopyToAsync(right);
                var toLeft = right.CopyToAsync(left);
                Task.WaitAll(toRight, toLeft);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static bool BackendIsOwnedBy(int backendPort, int processId)
    {
        var snapshot = WindowsTcpListenerSnapshot.Capture();
        return snapshot.Listeners.Any(listener =>
            listener.Port == backendPort && listener.ProcessId == processId);
    }
}
