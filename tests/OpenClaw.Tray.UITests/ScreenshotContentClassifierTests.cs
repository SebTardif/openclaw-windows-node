using System.Drawing;
using System.Drawing.Imaging;

namespace OpenClaw.Tray.UITests;

public sealed class ScreenshotContentClassifierTests
{
    [Fact]
    public void WindowChromeWithoutClientContent_IsRejected()
    {
        using var bitmap = new Bitmap(640, 400, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.DrawRectangle(Pens.Gray, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
        graphics.FillRectangle(Brushes.Black, bitmap.Width - 70, 8, 12, 12);
        graphics.FillRectangle(Brushes.Black, bitmap.Width - 45, 8, 12, 12);
        graphics.FillRectangle(Brushes.Black, bitmap.Width - 20, 8, 12, 12);

        Assert.False(AccessibilityAppFixture.HasMeaningfulVisualContent(bitmap));
    }

    [Fact]
    public void VariedClientContent_IsAccepted()
    {
        using var bitmap = new Bitmap(640, 400, PixelFormat.Format32bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.FillRectangle(Brushes.LightGray, 50, 80, 180, 270);
        graphics.FillRectangle(Brushes.DarkGray, 280, 110, 300, 60);
        graphics.DrawLine(Pens.Black, 300, 220, 550, 220);

        Assert.True(AccessibilityAppFixture.HasMeaningfulVisualContent(bitmap));
    }
}
