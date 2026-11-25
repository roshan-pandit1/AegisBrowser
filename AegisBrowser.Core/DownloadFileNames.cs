using System.Text;

namespace AegisBrowser.Core;

/// <summary>Conservative Windows download-name validation; does not authorize a path.</summary>
public static class DownloadFileNames
{
    public static bool TrySanitize(string? suggestion, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(suggestion) || suggestion.IndexOf(':') >= 0) return false;
        var components = suggestion.Split(['/', '\\']);
        if (components.Any(part => part is "." or "..")) return false;
        var candidate = components[^1];
        var cleaned = new StringBuilder(candidate.Length);
        foreach (var ch in candidate)
        {
            if (ch is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069') continue;
            if (char.IsControl(ch) || "<>\"|?*".Contains(ch)) return false;
            cleaned.Append(ch);
        }
        var value = cleaned.ToString();
        // Leave room for the parent path and any Chromium temporary suffix.
        if (value.Length is 0 or > 180 || value.EndsWith(' ') || value.EndsWith('.')) return false;
        var stem = value.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3'))) return false;
        name = value;
        return true;
    }
}
