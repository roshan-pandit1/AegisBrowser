namespace AegisBrowser.Core;

public enum PrivacyMode { Standard, Strict, Custom }

public sealed record PrivacyConfiguration
{
    public PrivacyMode Mode { get; init; } = PrivacyMode.Standard;
    public bool UseListsInCustom { get; init; }
    // Opt-in: apply third-party-qualified network rules when the top-level
    // site cannot be determined. Classification itself remains unknown.
    public bool ApplyThirdPartyRulesWhenSiteUnknown { get; init; }
    // Null preserves the existing Chromium preference for older profiles.
    // False explicitly restores Chromium's default after an opt-in is removed.
    public bool? BlockThirdPartyCookies { get; init; }
    // Opt-in Tor-inspired viewport bucketing; takes effect on next profile launch.
    // This does not alter screen metrics, browser API values or network identity.
    public bool LetterboxWebContent { get; init; }
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
