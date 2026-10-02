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
        public string? User { get; set; }
        public string? Host { get; set; }
        public int? RemotePort { get; set; }
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

    internal static void ClearBackend(int port)
    {
        lock (Gate)
        {
            if (!Listeners.TryGetValue(port, out var slot))
                return;
            slot.BackendPort = null;
            slot.BackendProcessId = null;
        }
    }

    internal static bool AllowsDestination(int port, string user, string host, int remotePort)
    {
        lock (Gate)
        {
            if (!Listeners.TryGetValue(port, out var slot))
                return true;
            if (slot.User is null || slot.Host is null || slot.RemotePort is null)
            {
                slot.User = user;
                slot.Host = host;
                slot.RemotePort = remotePort;
                return true;
            }

            return string.Equals(slot.User, user, StringComparison.Ordinal) &&
                string.Equals(slot.Host, host, StringComparison.Ordinal) &&
                slot.RemotePort == remotePort;
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
                if (!ConnectedBackendIsOwnedBy(backend, target, processId))
                    return;

                var left = client.GetStream();
                var right = backend.GetStream();
                using var stop = new CancellationTokenSource();
                var toRight = left.CopyToAsync(right, stop.Token);
                var toLeft = right.CopyToAsync(left, stop.Token);
                Task.WhenAny(toRight, toLeft).GetAwaiter().GetResult();
                stop.Cancel();
                client.Close();
                backend.Close();
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

    private static bool ConnectedBackendIsOwnedBy(TcpClient backend, int backendPort, int processId)
    {
        if (backend.Client.RemoteEndPoint is not IPEndPoint remote || remote.Port != backendPort)
            return false;
        if (remote.Address is not { } address || !IPAddress.IsLoopback(address))
            return false;

        var snapshot = WindowsTcpListenerSnapshot.Capture();
        var owners = snapshot.Listeners
            .Where(listener => listener.Port == backendPort && IsLoopback(listener.Address))
            .Select(listener => listener.ProcessId)
            .Distinct()
            .ToArray();
        return owners.Length == 1 && owners[0] == processId;
    }

    private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address);

    private static bool BackendIsOwnedBy(int backendPort, int processId)
    {
        var snapshot = WindowsTcpListenerSnapshot.Capture();
        return snapshot.Listeners.Any(listener =>
            listener.Port == backendPort && listener.ProcessId == processId);
    }
}
