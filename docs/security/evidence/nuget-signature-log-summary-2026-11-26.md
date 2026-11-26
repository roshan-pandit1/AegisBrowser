# Sanitized NuGet signature-log summary

Date: 2026-11-26. Artifact baseline:
`1ee1dd284d2f6ad3ccb66593ce2984d56843063a`.

**PARTIAL [SOURCE-VERIFIED] [DEVELOPER-VERIFIED]:** Twelve retained
`dotnet nuget verify` output files were inspected read-only. Their text
contains only the expected package/version line, public content and certificate
hashes, signature types, public Microsoft/NuGet certificate subjects and
validity dates. A strict line-format allowlist found zero unrecognized lines;
checks for private-key markers, credential assignments, personal paths and
signed URLs also found none. This is a bounded inspection, not proof that every
possible encoding of sensitive data would be detected.

The table records the **captured output-file** SHA-256, not the NuGet package
SHA-256. Package catalog hashes and official source URLs are recorded in
`../pr90-nuget-provenance.json`.

| Package | Version | Signature type shown | Captured output SHA-256 |
| --- | --- | --- | --- |
| CefSharp.Common.NETCore | 154.0.340 | Repository | `0E66191669F70725B7A102F66CA8C0A38374AE370A3BEA97D4BCB8FD78E35D07` |
| CefSharp.WinForms.NETCore | 154.0.340 | Repository | `756E508DE30CD8FB19F368F9CF4B77030EAD902E74F6A0BFC7EDCDE38E04EC64` |
| chromiumembeddedframework.runtime | 154.0.34 | Repository | `A7D13F61BFDFEF1B88A9CBAFA3FED8908AC928FAB3F5A7FCE6D43D06D3A22946` |
| chromiumembeddedframework.runtime.win-x64 | 154.0.34 | Repository | `233FD18C18EF0F3385D51B16ED916542E785F840C6238CDA089EC098A6B4E8BB` |
| Microsoft.Data.Sqlite | 10.0.15 | Author, repository | `46B28C6E72F6B851DB4770EF82804024E13458995098E9A044D44BA61943828A` |
| Microsoft.Data.Sqlite.Core | 10.0.15 | Author, repository | `0836A56FD29E4EF325FB283E16D64C4F5381B79B40A5F4B7E058D1E7ED227963` |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.15 | Author, repository | `A05BD706EC0131F5EFC55C7D3E1D3BCE5B602322C04B70A090B3AFB49E713FB7` |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.15 | Author, repository | `B363015EE3D586F73C5D03E951995E6E836F8E0E1BBF456CD14B559E38C84F82` |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Repository | `654C7E81D63B69CE6C9EC01007F4BC665A0186002E8304602DF8FA29E0DDE72C` |
| SQLitePCLRaw.core | 2.1.13 | Repository | `8F4AAEEEE5931DFF678874993B7E6747A19865B2F5949A8F69CD46ABABAAF756` |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Repository | `5B20DA5E0853007545F669D4E651C15070D5AEA8DF67194991A09B647887D849` |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Repository | `AB7F74D7622C2F7E8842AEAE2505D8B17B20925141CDAD916BF5A29D1A31572F` |

**Trust result: UNVERIFIED for all twelve [UNVERIFIED].** No warning or error
line appears in the retained output, but the individual command exit codes and
full trust-chain decisions were not preserved in those files. The separate
provenance JSON reports a passing developer execution; its report does not
replace the missing retained exit-code evidence. Next: in an authorized
environment, compare each exact package to the official catalog and run
`dotnet nuget verify --all` while recording its exit code, warnings,
signature types and trust-chain result without recording secrets.
