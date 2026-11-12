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
        if (available == 0) return 0;
        var cssAvailable = available / zoomFactor;
        if (cssAvailable < cssStep || !double.IsFinite(cssAvailable)) return available;

        // Chromium normally exposes an integer layout viewport by truncating
        // the CSS extent. Test the two neighboring control-unit sizes instead
        // of rounding the ideal size: rounding down can expose one CSS pixel
        // below the intended bucket (for example 583/0.8333 = 699.6).
        // This model cannot account for every DPI or renderer rounding mode;
        // those must be measured independently in the browser fixture.
        var count = Math.Floor((cssAvailable + 1e-9) / cssStep);
        if (count >= 9007199254740992d) return available; // integer precision exhausted
        var targetCss = count * cssStep;
        var desired = targetCss * zoomFactor;
        var integral = Math.Round(desired);
        if (Math.Abs(desired - integral) < 1e-7) desired = integral;
        var lower = Math.Clamp(Math.Floor(desired), 1, available);
        var upper = Math.Clamp(Math.Ceiling(desired), 1, available);
        var lowerError = Math.Abs(Math.Floor(lower / zoomFactor + 1e-7) - targetCss);
        var upperError = Math.Abs(Math.Floor(upper / zoomFactor + 1e-7) - targetCss);
        // Prefer the larger usable browsing area if both are equally close.
        return (int)(upperError <= lowerError ? upper : lower);
    }
}
