# Local profile manager and metadata

Use the new **Profiles** button from an open browser, or start the manager without a Chromium instance:

    AegisBrowser.exe --profiles

The window lists existing profile directories, offers a Create action for a new profile,
and launches a selected profile as a separate AegisBrowser process. Double-click opens
the selected profile. The default profile is always available to launch.

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
updates and rejects stale revisions. This milestone supplies the persistence model;
editing these fields and displaying folders/tags in the manager are later work.
The timestamps describe metadata creation and update, not the unknown creation time
of a legacy Chromium profile.

The manager still discovers old `profile-<name>` folders without changing them.
When an old profile is opened, the browser acquires its existing exclusive profile
lease, validates the paths, and registers that folder in the metadata database
before initializing CEF. Creating a profile in the manager creates its metadata as
part of the operation. A missing default profile is registered on first launch.
Older application versions continue to use the same directories and ignore the new
database. Slugs remain subject to the existing name policy and are not renamed here.
Linked profile folders and linked database paths are rejected; protect the profile
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
creation, invalid paths, a Windows junction, corrupt/newer databases, and recovery
from a killed process with an uncommitted SQLite transaction. It also retains the
existing directory/lease checks. The Windows workflow runs the native browser and
filtering suites, publish, smoke and portable packaging checks.

Manual Windows acceptance remains necessary: upgrade a copy of an existing default
and named profile containing cookies, IndexedDB, local storage and privacy/search
settings; verify login and settings after restart and rollback; try a locked profile,
unwritable metadata/backup location and recovery from a corrupt database; check
manager keyboard operation and 100/150/200% scaling; and close multiple active
profiles normally while verifying that their locks are released. Do not infer a
manual pass from the automated suites.
