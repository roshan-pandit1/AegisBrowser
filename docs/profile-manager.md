# Local profile manager and metadata

Use the **Profiles** button from an open browser, or start the manager without a Chromium instance:

    AegisBrowser.exe --profiles

The dashboard lists profiles in a table, including name, GUID, storage slug, tags,
folders, metadata update time and observed status. Select a row for details or use
its Launch, Edit and Actions buttons. **New profile** creates a storage slug and
metadata together. Editing changes the display name, notes, tags and folders without
renaming the storage directory. Enter one tag or folder per line. The editor checks
the original metadata revision when saving: if another manager has changed it,
your text remains in the editor and you must reopen the profile to reload it.

Search matches display name, slug, tags and folders; status and folder filters and
name/update-time sorting only change the view. Use Ctrl+F for search, Ctrl+N for a
new profile, F5 to refresh, and Enter on a table row to launch (or activate its
focused Edit or Actions cell). Clear
filters restores the full list. The table scrolls horizontally when necessary.
The running filter includes Starting, Running and In use; the stopped filter includes
Stopped and Failed. Missing and Unknown profiles remain visible under All statuses.

The dashboard reads profile status and metadata in the background and refreshes
approximately every three seconds. **Running** means a manager-launched child is
alive and the lease is held; the lease owner is not independently verified. **In
use** means the lease is held without a child tracked by this manager. **Starting**
means a launched process has not yet been observed with the lease, not that CEF is
ready; check for a browser startup dialog if it persists. **Unknown** means the lock
could not be inspected, so launch is disabled. **Failed** reports a nonzero exit
from a child this manager launched. An unheld lock file means stopped; the file is
never removed by the dashboard. External processes are recognized by their lease,
not by PID or window enumeration.

The manager rejects duplicate starts it observes. Two managers can race between
checking a lease and starting children; the browser's exclusive lease remains the
final arbiter and prevents both from opening the same profile. The manager never
force-stops a process. Close the browser normally to release its lease. Closing the
dashboard leaves browser processes running.

## Clean duplication and templates

Select a profile and choose **Actions → Duplicate clean profile**. Enter a new slug
and display name. Tags and folders are copied; notes and the privacy/search settings
are separate opt-in choices. A new GUID is assigned. The new profile has fresh
Chromium storage: cookies, history, credentials, local storage and caches are never
copied. Metadata-only duplication can use a running source. To copy privacy or search
settings, close the source first; the operation holds its profile lease while reading
those settings. Invalid, linked or unreadable settings are rejected.

Choose **Actions → Save as template** to save metadata labels and optionally the
profile's privacy/search settings. **Templates** in the sidebar can search, edit,
duplicate and delete templates or create new profiles from them. Deletion asks for
confirmation and does not remove profiles. Template edits and deletion check their
revision, and a template cannot replace an existing profile. Templates use versioned
JSON files under `Profiles\templates`, with a separate interprocess lock and atomic
file replacement. They contain no Chromium profile data. Their descriptions, tags,
folders and optional privacy rules can contain personal information; protect the
files using the Windows account. No template encryption is provided.

Full browser-data duplication is **unavailable**. A closed profile copy has not been
validated across CefSharp/Chromium versions, Windows account boundaries or credential
encryption. Windows DPAPI data is typically tied to the user and machine
([Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata));
Chrome also documents application-bound cookie encryption on Windows
([Google](https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html)).
Those Chrome details do not establish which protection this CefSharp build uses.
Do not copy or attempt to decrypt Chromium databases as a workaround.

New profiles are prepared in a `.creating-<id>` directory before publication. If an
operation fails after writing staged settings, that clearly named directory may remain
for inspection; it is not listed as a profile. If publication succeeds but the metadata
commit fails, the profile directory is retained and can be adopted on a later launch.
Do not remove an existing `profile-*` directory as cleanup. Back up and inspect any
leftover staging directory before manually removing it.

## Bulk labels and clean copies

Use the Select checkbox beside registered profiles, **Select visible**, or **Clear
selection**. Selection is tracked by profile GUID and remains selected when filters
or periodic refresh change the visible rows. Directory-only profiles must first be
opened or edited so they have metadata. **Bulk labels** adds or removes tags or folder
assignments; enter one label per line. **Clean copy selected** generates a distinct
`-copy` slug for each source and excludes notes, settings and Chromium storage.

Each selected profile is updated independently with its original revision. Results
show success, failure or cancellation for each profile; a conflict leaves that profile
unchanged and requires refresh before retrying. Cancel stops work before the next
profile and does not undo changes already committed. The operation does not delete
profiles or stop browsers.

Directory-only profiles remain visible as **Not registered** until opened or edited.
Editing one first requires its profile lease and creates the same validated metadata
backup used during migration. If it is already in use, close it before editing. The
default profile can be launched before its directory exists. A registered profile
whose directory has gone missing cannot be launched into a new empty directory;
restore its directory first.

Names follow the existing 1–40 ASCII character policy (letters, digits, hyphens,
underscores), case-insensitively. The manager uses the existing
`%LOCALAPPDATA%\AegisBrowser\Profiles\profile-<name>` layout. New directories are
staged and moved into place rather than overwriting existing data. The browser's
exclusive per-profile lock still rejects concurrent launches of the same profile.
Profile metadata migration does not move, clone, rename, delete or directly edit
Chromium caches or the existing privacy and search settings.

## Metadata and migration

The manager stores application-owned metadata in `profile-metadata.sqlite3` under
`%LOCALAPPDATA%\AegisBrowser\Profiles`, separate from every Chromium profile folder.
The database uses SQLite WAL and schema version 1. Each registered profile has a
stable GUID, its existing case-insensitive ASCII directory slug, a separate display
name, notes, tags, folders, timestamps for the metadata, and a revision. Display
names may coincide; IDs and slugs distinguish profiles. The repository validates
updates and rejects stale revisions. Notes are shown in the selected-profile details
and editor but are not included in search or diagnostic output.
The timestamps describe metadata creation and update, not the unknown creation time
of a legacy Chromium profile.

The manager still discovers old `profile-<name>` folders without changing them.
When an old profile is opened, the browser acquires its existing exclusive profile
lease, validates the paths, and registers that folder in the metadata database
before initializing CEF. Creating a profile in the manager creates its metadata as
part of the operation. A missing default profile is registered on first launch.
Older application versions continue to use the same directories and ignore the new
database. Slugs remain subject to the existing name policy and are not renamed here.
Linked profile folders, profile lock files and database paths are rejected; protect the profile
root with the Windows account's normal permissions. These path checks are not a
defense against an attacker who can concurrently replace files in that account.

Before each legacy folder is registered, Aegis creates a consistent SQLite backup
in `Profiles\metadata-backups` and checks its schema, integrity, relationships and
profile count. A failed backup or transaction leaves the legacy directory available
for a later retry and does not register a partial record. If a newly created folder
was published but its metadata commit failed, it is deliberately retained and can
be adopted on a later launch. A corrupt, unrecognized, or newer-version database is
preserved and startup fails with an error instead of replacing it. Unfinished
temporary files are not treated as profiles or as authoritative metadata.

Backups contain metadata, including private notes and labels; protect them like the
profile data. No automatic backup retention or encryption is provided. The first
backup for an installation with only directory-based profiles contains an empty v1
metadata database because the previous version had no metadata database to copy.
The browser files themselves are not backed up by this migration.

### Rollback and recovery

Close **all** Aegis Browser windows and profile managers before handling metadata.
Keep a separate copy of the whole `Profiles` directory, including any
`profile-metadata.sqlite3-wal` and `profile-metadata.sqlite3-shm` files left after a
crash. To roll back metadata, move the current database and any WAL/SHM sidecars to
that safe backup location, then copy the selected `metadata-backups\*.sqlite3` file
to `Profiles\profile-metadata.sqlite3`. Reopen Aegis Browser and verify the profiles.
This restores only the metadata state captured before that migration: later metadata
edits and IDs assigned after the snapshot will be lost, and still-unregistered folders
will be adopted again on opening. Chromium profile folders remain in place. Keep the
original corrupt or newer database and sidecars for repair; do not replace them with
an empty database if their metadata is needed. If file permissions prevent a backup
or database write, restore access and retry rather than removing a profile folder.

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

The console suite covers directory-only migration, metadata round trips and revision
conflicts, backup restoration, failed migration and retry, duplicate and concurrent
creation, invalid paths, Windows junctions and a read-only database, corrupt/newer
databases, and recovery from a killed process with an uncommitted SQLite transaction.
Dashboard tests also cover creating and editing metadata, stale revisions, search,
filtering, sorting, duplicate launches, and a separate helper process acquiring and
releasing the actual OS profile lease. It also retains existing directory/lease checks.
The Windows native browser suite exercises the dashboard and editor controls, including
an edit conflict. The lease helper is not a Chromium process. After publishing, CI also
launches the actual browser through the dashboard service, rejects a duplicate, requests
normal window closure, confirms lease release and reopens the same profile. The separate CEF suite
creates two CEF profiles without metadata, migrates them before restarting CEF, and
checks their cookie/localStorage isolation, persistence and stable IDs over repeated
process restarts. The workflow also runs filtering, publish, smoke and packaging checks.

Manual Windows acceptance remains necessary: use the dashboard with keyboard-only
navigation and 100/150/200% scaling; launch, close and reopen multiple profiles and
check external launches and duplicate-start messages; terminate a disposable browser
unexpectedly and verify status and lock recovery. Upgrade a copy of an existing default
and named profile containing cookies, IndexedDB, local storage and privacy/search
settings; verify login and settings after restart and rollback; try a locked profile,
unwritable metadata/backup location and recovery from a corrupt database; check
manager keyboard operation and 100/150/200% scaling; and close multiple active
profiles normally while verifying that their locks are released. Do not infer a
manual pass from the automated suites.
