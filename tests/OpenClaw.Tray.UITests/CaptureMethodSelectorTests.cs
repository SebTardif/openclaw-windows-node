using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Deterministic tests for the capture-method fallback order used by
/// <see cref="AccessibilityAppFixture.CaptureHubScreenshotIfRequested"/>:
/// Windows.Graphics.Capture app-window capture first, then PrintWindow, then
/// the safety-gated screen-copy fallback. These use fake attempts so the
/// selection logic is exercised without any real Windows API or desktop.
/// </summary>
public sealed class CaptureMethodSelectorTests
{
    private const string Wgc = "WindowsGraphicsCapture";
    private const string PrintWindow = "PrintWindow";
    private const string CopyFromScreen = "CopyFromScreen";

    [Fact]
    public void ValidWindowsGraphicsCapture_ShortCircuitsPrintWindowAndScreenCopy()
    {
        var printWindowCalled = false;
        var screenCopyCalled = false;
        var diagnostics = new List<string>();

        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => Meaningful()),
            new CaptureMethodSelector.MethodAttempt(PrintWindow, () => { printWindowCalled = true; return Meaningful(); }),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreen, () => { screenCopyCalled = true; return Meaningful(); }),
        };

        var (method, bitmap) = CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics);
        using (bitmap)
        {
            Assert.Equal(Wgc, method);
        }
        Assert.False(printWindowCalled, "PrintWindow must not be attempted once WGC succeeds.");
        Assert.False(screenCopyCalled, "CopyFromScreen must not be attempted once WGC succeeds.");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BlankWindowsGraphicsCapture_FallsBackToPrintWindow()
    {
        var diagnostics = new List<string>();
        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => Blank()),
            new CaptureMethodSelector.MethodAttempt(PrintWindow, () => Meaningful()),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreen, () => throw new InvalidOperationException("must not be reached")),
        };

        var (method, bitmap) = CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics);
        using (bitmap)
        {
            Assert.Equal(PrintWindow, method);
        }
        Assert.Contains(diagnostics, d => d.StartsWith(Wgc, StringComparison.Ordinal));
    }

    [Fact]
    public void FailedWindowsGraphicsCapture_FallsBackToPrintWindow()
    {
        var diagnostics = new List<string>();
        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => throw new InvalidOperationException("GraphicsCaptureSession.IsSupported() returned false")),
            new CaptureMethodSelector.MethodAttempt(PrintWindow, () => Meaningful()),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreen, () => throw new InvalidOperationException("must not be reached")),
        };

        var (method, bitmap) = CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics);
        using (bitmap)
        {
            Assert.Equal(PrintWindow, method);
        }
        Assert.Contains(diagnostics, d => d.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedWindowsGraphicsCaptureAndPrintWindow_FallsBackToSafeScreenCopy()
    {
        var diagnostics = new List<string>();
        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => Blank()),
            new CaptureMethodSelector.MethodAttempt(PrintWindow, () => (Bitmap?)null),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreen, () => Meaningful()),
        };

        var (method, bitmap) = CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics);
        using (bitmap)
        {
            Assert.Equal(CopyFromScreen, method);
        }
        Assert.Equal(2, diagnostics.Count);
    }

    [Fact]
    public void AllMethodsFailedOrBlank_Throws()
    {
        var diagnostics = new List<string>();
        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => Blank()),
            new CaptureMethodSelector.MethodAttempt(PrintWindow, () => (Bitmap?)null),
            new CaptureMethodSelector.MethodAttempt(CopyFromScreen, () => throw new InvalidOperationException("refused, Hub window is obscured")),
        };

        var ex = Assert.Throws<AllCaptureMethodsFailedException>(
            () => CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics));

        Assert.Equal(3, ex.Diagnostics.Count);
        Assert.Equal(3, diagnostics.Count);
        // The publishable exception message must stay generic; only the
        // Diagnostics collection carries method-specific detail.
        Assert.DoesNotContain("obscured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBlankRejection_StillRejectsWindowChromeOnlyContent()
    {
        using var bitmap = new Bitmap(640, 400, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.DrawRectangle(Pens.Gray, 0, 0, bitmap.Width - 1, bitmap.Height - 1);

        Assert.False(AccessibilityAppFixture.HasMeaningfulVisualContent(bitmap));

        var diagnostics = new List<string>();
        var attempts = new[]
        {
            new CaptureMethodSelector.MethodAttempt(Wgc, () => new Bitmap(bitmap)),
        };

        Assert.Throws<AllCaptureMethodsFailedException>(
            () => CaptureMethodSelector.SelectFirstMeaningful(attempts, AccessibilityAppFixture.HasMeaningfulVisualContent, diagnostics));
    }

    private static Bitmap Meaningful()
    {
        var bitmap = new Bitmap(640, 400, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.FillRectangle(Brushes.LightGray, 50, 80, 180, 270);
        graphics.FillRectangle(Brushes.DarkGray, 280, 110, 300, 60);
        graphics.DrawLine(Pens.Black, 300, 220, 550, 220);
        return bitmap;
    }

    private static Bitmap Blank()
    {
        var bitmap = new Bitmap(640, 400, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        return bitmap;
    }
}
