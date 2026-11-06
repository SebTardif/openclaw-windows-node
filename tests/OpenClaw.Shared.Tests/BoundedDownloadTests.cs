using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Audio;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class BoundedDownloadTests
{
    [Fact]
    public async Task CopyAsync_StopsWhenTheBodyPassesThePinnedSize()
    {
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("abcdef"));
        await using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BoundedDownload.CopyAsync(source, destination, pinnedSize: 4, "model", CancellationToken.None));

        Assert.Contains("pinned size of 4", ex.Message);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task CopyAsync_WritesABodyThatFitsThePinnedSize()
    {
        var payload = Encoding.ASCII.GetBytes("abcd");
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        await BoundedDownload.CopyAsync(source, destination, pinnedSize: 4, "model", CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task CopyAsync_HonorsTheCallCancelToken()
    {
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("abcdef"));
        await using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedDownload.CopyAsync(source, destination, pinnedSize: 100, "model", cts.Token));
    }
}
