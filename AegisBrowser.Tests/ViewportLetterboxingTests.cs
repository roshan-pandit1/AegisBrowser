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
        Check(ViewportLetterboxing.Calculate(1000, 600, 1) == new ViewportRectangle(20, 0, 960, 600),
            "zoom in chooses CSS buckets that fit the centered control");
        Check(ViewportLetterboxing.Calculate(1000, 600, -1) == new ViewportRectangle(0, 8, 1000, 583),
            "zoom out chooses CSS buckets with rounded control dimensions");
        Check(ViewportLetterboxing.Calculate(200, 100, 1) == new ViewportRectangle(0, 0, 200, 100),
            "zoomed panes below the first CSS bucket remain usable");
        Check(ViewportLetterboxing.Calculate(0, 0, 1) == new ViewportRectangle(0, 0, 0, 0),
            "zoomed empty construction pane");
        foreach (var zoom in new[] { -3d, -1d, 0d, 1d, 3d })
        {
            var rect = ViewportLetterboxing.Calculate(1075, 632, zoom);
            var factor = Math.Pow(1.2, zoom);
            Check(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= 1075 && rect.Y + rect.Height <= 632,
                "zoomed viewport stays in pane");
            Check(Math.Abs(rect.Width / factor / ViewportLetterboxing.WidthStep -
                Math.Round(rect.Width / factor / ViewportLetterboxing.WidthStep)) * ViewportLetterboxing.WidthStep < 1,
                "zoomed width approximates CSS bucket");
            Check(Math.Abs(rect.Height / factor / ViewportLetterboxing.HeightStep -
                Math.Round(rect.Height / factor / ViewportLetterboxing.HeightStep)) * ViewportLetterboxing.HeightStep < 1,
                "zoomed height approximates CSS bucket");
        }
        foreach (var zoom in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 10000d })
        {
            var invalidRejected = false;
            try { ViewportLetterboxing.Calculate(1000, 600, zoom); }
            catch (ArgumentOutOfRangeException) { invalidRejected = true; }
            Check(invalidRejected, "invalid zoom rejected");
        }
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
