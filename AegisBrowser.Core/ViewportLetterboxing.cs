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

    public static ViewportRectangle Calculate(int availableWidth, int availableHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(availableHeight);

        // When a pane is too small for a full bucket, preserve its available
        // size rather than making the browsing surface unusable or invisible.
        var width = availableWidth < WidthStep ? availableWidth : availableWidth - availableWidth % WidthStep;
        var height = availableHeight < HeightStep ? availableHeight : availableHeight - availableHeight % HeightStep;
        return new((availableWidth - width) / 2, (availableHeight - height) / 2, width, height);
    }
}
