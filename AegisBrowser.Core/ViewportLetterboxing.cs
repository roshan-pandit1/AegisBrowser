namespace AegisBrowser.Core;

/// <summary>A content area centered within its available WinForms client area.</summary>
public readonly record struct ViewportRectangle(int X, int Y, int Width, int Height);

/// <summary>
/// Optional viewport-size bucketing inspired by Tor Browser letterboxing.
/// This only changes the hosted Chromium control's dimensions; it does not
/// spoof screen properties or modify the browser engine.
/// </summary>
public static class ViewportLetterboxing
{
    public const int WidthStep = 200;
    public const int HeightStep = 100;

    /// <param name="zoomLevel">Chromium page zoom level; zero is the default.</param>
    public static ViewportRectangle Calculate(int availableWidth, int availableHeight, double zoomLevel = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(availableHeight);
        if (!double.IsFinite(zoomLevel)) throw new ArgumentOutOfRangeException(nameof(zoomLevel));
        // Chromium's page zoom factor is 1.2 raised to its zoom level. Control
        // units and CSS pixels may also differ with DPI; only real browser
        // measurements can establish their relationship on a given display.
        var zoomFactor = Math.Pow(1.2, zoomLevel);
        if (!double.IsFinite(zoomFactor) || zoomFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(zoomLevel));

        // When a pane is too small for a full bucket, preserve its available
        // size rather than making the browsing surface unusable or invisible.
        var width = FitBucket(availableWidth, WidthStep, zoomFactor);
        var height = FitBucket(availableHeight, HeightStep, zoomFactor);
        return new((availableWidth - width) / 2, (availableHeight - height) / 2, width, height);
    }

    private static int FitBucket(int available, int cssStep, double zoomFactor)
    {
        if (available == 0 || available / zoomFactor < cssStep) return available;
        // Round the physical control size once, after selecting a CSS bucket.
        // Fractional factors can cause a one-pixel CSS rounding difference.
        var count = Math.Floor((available / zoomFactor + 1e-9) / cssStep);
        var size = Math.Round(count * cssStep * zoomFactor, MidpointRounding.AwayFromZero);
        return size < 1 ? available : (int)Math.Min(available, size);
    }
}
