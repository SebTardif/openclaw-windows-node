using System;
using System.Collections.Generic;
using System.Drawing;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Pure capture-method fallback selection logic for the Hub screenshot
/// witness. Kept independent of any real Windows API so the fallback order
/// (Windows.Graphics.Capture app-window capture first, then PrintWindow,
/// then a safety-gated screen copy) is deterministically testable with fake
/// attempts, without a real desktop.
/// </summary>
internal static class CaptureMethodSelector
{
    /// <summary>One capture method's attempt: a name plus a function that performs the capture (or returns null/throws on failure).</summary>
    internal readonly record struct MethodAttempt(string Method, Func<Bitmap?> Capture);

    /// <summary>
    /// Tries each attempt in order. The first attempt that produces a
    /// non-null bitmap satisfying <paramref name="isMeaningful"/> short-
    /// circuits the rest and is returned. Every skipped/failed/blank attempt
    /// appends a diagnostic line to <paramref name="diagnostics"/>. If every
    /// attempt fails or is blank, throws <see cref="AllCaptureMethodsFailedException"/>
    /// with the aggregated diagnostics.
    /// </summary>
    internal static (string Method, Bitmap Bitmap) SelectFirstMeaningful(
        IEnumerable<MethodAttempt> attempts,
        Func<Bitmap, bool> isMeaningful,
        List<string> diagnostics)
    {
        foreach (var attempt in attempts)
        {
            Bitmap? bitmap;
            try
            {
                bitmap = attempt.Capture();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{attempt.Method}: {ex.GetType().Name} hresult=0x{ex.HResult:X8} message={ex.Message}");
                continue;
            }

            if (bitmap == null)
            {
                diagnostics.Add($"{attempt.Method}: not attempted or produced no image");
                continue;
            }

            if (isMeaningful(bitmap))
                return (attempt.Method, bitmap);

            diagnostics.Add($"{attempt.Method}: blank or near-uniform");
            bitmap.Dispose();
        }

        throw new AllCaptureMethodsFailedException(new List<string>(diagnostics));
    }
}

/// <summary>
/// Thrown when every configured capture method failed or produced a blank
/// image. The exception message is intentionally generic and safe to
/// surface in redacted proof output; <see cref="Diagnostics"/> carries the
/// full method-specific detail (exception type/HResult/message per attempt)
/// for private test output only.
/// </summary>
internal sealed class AllCaptureMethodsFailedException : InvalidOperationException
{
    public IReadOnlyList<string> Diagnostics { get; }

    public AllCaptureMethodsFailedException(IReadOnlyList<string> diagnostics)
        : base("Hub screenshot capture failed: every configured capture method (WindowsGraphicsCapture, PrintWindow, safe screen-copy fallback) was unavailable or produced a blank image.")
    {
        Diagnostics = diagnostics;
    }
}
