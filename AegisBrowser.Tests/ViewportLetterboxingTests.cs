using AegisBrowser.Core;

internal static class ViewportLetterboxingTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL viewport letterboxing: " + name);
            count++;
        }

        Check(ViewportLetterboxing.Calculate(1200, 800) == new ViewportRectangle(0, 0, 1200, 800),
            "already bucketed");
        Check(ViewportLetterboxing.Calculate(1250, 750) == new ViewportRectangle(25, 25, 1200, 700),
            "centered with horizontal and vertical padding");
        Check(ViewportLetterboxing.Calculate(750, 450) == new ViewportRectangle(75, 25, 600, 400),
            "small supported browser window");
        Check(ViewportLetterboxing.Calculate(399, 199) == new ViewportRectangle(99, 49, 200, 100),
            "just below next size bucket");
        Check(ViewportLetterboxing.Calculate(400, 200) == new ViewportRectangle(0, 0, 400, 200),
            "exact bucket boundary");
        Check(ViewportLetterboxing.Calculate(401, 201) == new ViewportRectangle(0, 0, 400, 200),
            "odd remainder stays within pane");
        Check(ViewportLetterboxing.Calculate(199, 99) == new ViewportRectangle(0, 0, 199, 99),
            "tiny panes remain usable");
        Check(ViewportLetterboxing.Calculate(0, 0) == new ViewportRectangle(0, 0, 0, 0),
            "empty pane during construction");
        foreach (var (width, height) in new[] { (200, 100), (399, 199), (750, 450), (1920, 1080), (3200, 1800) })
        {
            var rect = ViewportLetterboxing.Calculate(width, height);
            Check(rect.X >= 0 && rect.Y >= 0 && rect.Width >= 0 && rect.Height >= 0
                && rect.X + rect.Width <= width && rect.Y + rect.Height <= height,
                "viewport bounds remain inside host pane");
            Check(rect.Width < 200 || rect.Width % 200 == 0,
                "width quantized for usable panes");
            Check(rect.Height < 100 || rect.Height % 100 == 0,
                "height quantized for usable panes");
        }
        var negativeRejected = false;
        try { ViewportLetterboxing.Calculate(-1, 100); }
        catch (ArgumentOutOfRangeException) { negativeRejected = true; }
        Check(negativeRejected, "negative width rejected");
        negativeRejected = false;
        try { ViewportLetterboxing.Calculate(100, -1); }
        catch (ArgumentOutOfRangeException) { negativeRejected = true; }
        Check(negativeRejected, "negative height rejected");
        Console.WriteLine($"PASS: {count} viewport letterboxing checks");
    }
}
