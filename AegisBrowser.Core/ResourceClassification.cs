namespace AegisBrowser.Core;

// Names already observed by the existing CEF adapter. Unknown future resource values remain
// "other" for rule compatibility but explicitly carry an inspection-coverage warning.
public static class ResourceClassification
{
    public static (string Type, bool Unknown) FromCefName(string name) => name switch
    {
        "MainFrame" => ("other", false), "SubFrame" => ("subdocument", false),
        "Script" => ("script", false), "Stylesheet" => ("stylesheet", false),
        "Image" or "Favicon" => ("image", false), "FontResource" => ("font", false),
        "Media" => ("media", false), "Xhr" => ("xmlhttprequest", false), "Ping" => ("ping", false),
        _ => ("other", true)
    };
}
