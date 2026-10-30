# Proxy configuration foundation

This first P3 slice stores proxy configuration and credentials. It does **not** configure CefSharp networking, test a proxy, expose a dashboard editor, or establish network isolation. HTTP, HTTPS and SOCKS5 are configuration values, not supported browser transports yet. There is no claim of fail-closed network behavior after a browser starts.

An existing profile with no proxy record retains its previous startup behavior. A profile with a required proxy, an interrupted proxy update, invalid proxy metadata or an unsupported proxy database version is stopped before CEF initialization. A saved Direct record is an explicit choice; invalid proxy input never becomes Direct. The application cannot currently edit these records through its UI. Configuration changes require the matching closed-profile lease, the current profile metadata snapshot and the expected proxy revision.

## Storage and recovery

`proxy-config.sqlite3` is a separate version-1 application database under the profiles root. It contains profile and proxy GUIDs, protocol, endpoint, label, authentication requirement, opaque credential reference, timestamps and revision. It contains no username or password. It does not migrate or modify the existing profile metadata database or Chromium storage. A `.aegis-proxy-required` guard in the profile directory is created before a required configuration is committed; an explicit Direct update is committed before its guard is removed. A crash between those steps leaves startup blocked. Proxy configuration and credential changes require the existing exclusive profile lease.

If the database is corrupt, missing while a guard exists, or has an unsupported version, preserve the database, its `-journal`/`-wal`/`-shm` files and the guard. Do not delete a guard to work around a failure. Recover from a verified copy using a compatible management tool, then explicitly select Direct only if that is the desired connection. This foundation does not yet provide a proxy database backup, repair UI or automated recovery. Existing metadata backups and profile exports **do not include proxy configurations or credentials**. Clean duplication, templates and metadata import likewise do not copy them; a newly created profile uses the existing default connection behavior. Review that behavior before opening an imported or duplicated profile.

Older AegisBrowser versions do not recognize the guard or proxy database. Before rolling back to an older executable, explicitly return affected profiles to Direct with a compatible management tool and confirm that no operation is pending. This is a compatibility limitation, not OS-level network containment.

## Credentials

The local vault uses Windows DPAPI `CurrentUser` protection with an opaque credential GUID and profile GUID bound as additional entropy. Ciphertext is stored under `.proxy-credentials/<profile-guid>/<credential-guid>.bin`, outside Chromium profile directories and separate from SQLite. Replacement publishes new encrypted material before changing its reference; the old file is removed only after the configuration commit succeeds. If the outcome of a save is uncertain, newly written encrypted material is retained for manual inspection rather than risking deletion of a referenced credential. A failed post-commit cleanup returns a warning. Plaintext credentials are excluded from configuration, normal exports, templates and metadata backups. Neither DPAPI ciphertext nor credential references are portable exports.

DPAPI depends on the Windows user profile and may become unavailable after account or key recovery problems. A different Windows account cannot normally decrypt the data; software already running as the same user is not isolated by DPAPI. Managed username/password strings can remain in process memory. This feature does not implement proxy authentication, a password-reveal UI, or credential sharing between profiles.

## Runtime investigation still required

The repository pins CefSharp 154.0.340. Its [versioned `IRequestContext` source](https://github.com/cefsharp/CefSharp/blob/v154.0.340/CefSharp/IRequestContext.cs) documents that preference operations require the CEF UI thread, and its [request handler source](https://github.com/cefsharp/CefSharp/blob/v154.0.340/CefSharp/Handler/RequestHandler.cs) exposes proxy challenge context. These APIs alone do not establish routing or isolation. The [Chromium proxy documentation](https://chromium.googlesource.com/chromium/src/+/main/net/docs/proxy.md) describes SOCKS5 as TCP-only and without SOCKS5 authentication in Chrome; confirm behavior against the pinned CEF runtime and controlled fixtures before supporting any transport. [Microsoft's DPAPI documentation](https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection) describes the current-user protection boundary.

Later work must independently test proxy authentication scope, HTTP/HTTPS/SOCKS5 routing, redirects, subresources, popups, offline transitions, local bypass behavior, DNS, IPv4/IPv6, WebRTC and non-HTTP traffic. A successful connection test alone cannot prove absence of direct network traffic. OS-level containment may be required for stronger guarantees.

## Manual Windows verification still required

- On a test account, verify credential replacement and loss of access after moving protected data to a different Windows user.
- Test interrupted writes, corrupt/missing database, explicit Direct recovery and rollback to an older build using disposable profiles.
- Verify real proxy routing and failure transitions only after a browser integration PR provides controlled native network tests.
