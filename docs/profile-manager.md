# Local profile manager (first milestone)

Use the new **Profiles** button from an open browser, or start the manager without a Chromium instance:

    AegisBrowser.exe --profiles

The window lists existing profile directories, offers a Create action for a new profile,
and launches a selected profile as a separate AegisBrowser process. Double-click opens
the selected profile. The default profile is always available to launch.

Names follow the existing 1–40 ASCII character policy (letters, digits, hyphens,
underscores), case-insensitively. The manager uses the existing
`%LOCALAPPDATA%\\AegisBrowser\\Profiles\\profile-<name>` layout and does not migrate,
clone, rename, delete or modify Chromium caches. New directories are staged and moved
into place rather than overwriting existing data. The browser's existing exclusive
per-profile lock still rejects concurrent launches of the same profile.

This is an initial **local** profile manager, not GoLogin fingerprint compatibility.
It does not implement proxies, fingerprint modifications, cloud sync or team sharing.
Existing profiles may contain unencrypted browsing data and should be protected by
the Windows account. For profile deletion, close every window before manually removing
data; no Delete button is offered by the manager.

## Verification

On Windows, run:

    dotnet run --project AegisBrowser.Tests -c Release
    dotnet run --project AegisBrowser -c Release -- --profiles

Create a profile, open it, verify that storage persists after restart, and ensure
that opening the same profile a second time is rejected by the existing lease.
Confirm previously existing profile folders remain intact. In the installed browser,
select Profiles on the toolbar and confirm the manager launches separate processes.

The console test covers empty listings, name normalization, duplicate rejection,
invalid/traversal names, temporary/invalid directories and existing data preservation.
The normal browser integration suite and manual Windows GUI acceptance remain necessary.
