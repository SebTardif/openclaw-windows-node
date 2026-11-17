using System.IO;

namespace OpenClaw.Shared.Audio;

internal static class BoundedDownload
{
    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        long pinnedSize,
        string assetName,
        CancellationToken cancellationToken,
        IProgress<(long downloaded, long total)>? progress = null,
        long reportedTotal = 0)
    {
        var buffer = new byte[81920];
        long downloaded = 0;
        var total = reportedTotal > 0 ? reportedTotal : pinnedSize;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                return;

            downloaded += bytesRead;
            if (pinnedSize > 0 && downloaded > pinnedSize)
            {
                throw new InvalidOperationException(
                    $"Download of '{assetName}' exceeded the pinned size of {pinnedSize} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            progress?.Report((downloaded, total));
        }
    }
}
