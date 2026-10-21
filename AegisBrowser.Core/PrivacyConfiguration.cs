namespace AegisBrowser.Core;

public enum PrivacyMode { Standard, Strict, Custom }

public sealed record PrivacyConfiguration
{
    public PrivacyMode Mode { get; init; } = PrivacyMode.Standard;
    public bool UseListsInCustom { get; init; }
    public string FilterList { get; init; } = StarterList;
    public string UserRules { get; init; } = "";
    public string[] BlockedDomains { get; init; } = [];
    public string[] AllowedDomains { get; init; } = [];
    public string[] SiteExceptions { get; init; } = [];

    // Deliberately small, authored starter list. Not advertised as all of EasyPrivacy.
    public const string StarterList = "! Aegis starter tracking list v1\n"
        + "||google-analytics.com^$third-party\n||analytics.google.com^$third-party\n"
        + "||doubleclick.net^$third-party\n||scorecardresearch.com^$third-party\n"
        + "||quantserve.com^$third-party\n||hotjar.com^$third-party\n";
}
