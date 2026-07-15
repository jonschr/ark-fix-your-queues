using System.Drawing;

namespace ArkFixYourQueues.Tests;

public sealed class WindowsInteropTests
{
    [Fact]
    public void SessionBrowserRequiresOrangeInJoinLastPlayedRegion()
    {
        using var screen = SolidScreen(320, 180, Color.Black);
        Fill(screen, .82, .98, .80, .90, Color.FromArgb(180, 80, 20));

        Assert.True(WindowsInterop.LooksLikeSessionBrowser(screen));
    }

    [Fact]
    public void MainMenuUsesCenteredDesignViewportOnTallWindow()
    {
        using var screen = SolidScreen(320, 240, Color.Black);
        // A 320x240 capture has a centered 320x180 design viewport (30 px bars).
        Fill(screen, .28, .72, .3125, .6792, Color.FromArgb(20, 80, 120));

        Assert.True(WindowsInterop.LooksLikeMainMenu(screen));
    }

    [Fact]
    public void LoadingGlobeNeedsDarkPurpleAndBrightPixels()
    {
        using var screen = SolidScreen(320, 180, Color.FromArgb(10, 10, 10));
        FillPixels(screen, 600, Color.FromArgb(70, 20, 100));
        FillPixels(screen, 100, Color.White, start: 600);

        Assert.True(WindowsInterop.LooksLikeLoadingGlobe(screen));
    }

    [Fact]
    public void PlainDarkScreenIsNotLoadingGlobe()
    {
        using var screen = SolidScreen(320, 180, Color.Black);

        Assert.False(WindowsInterop.LooksLikeLoadingGlobe(screen));
    }

    private static Bitmap SolidScreen(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static void Fill(Bitmap bitmap, double left, double right, double top, double bottom, Color color)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.FillRectangle(new SolidBrush(color),
            (int)(bitmap.Width * left), (int)(bitmap.Height * top),
            (int)(bitmap.Width * (right - left)), (int)(bitmap.Height * (bottom - top)));
    }

    private static void FillPixels(Bitmap bitmap, int count, Color color, int start = 0)
    {
        for (var index = start; index < start + count; index++)
            bitmap.SetPixel(index % bitmap.Width, index / bitmap.Width, color);
    }
}
