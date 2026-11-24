# Bookmarks

The implementation is a disabled prototype: normal application startup does
not supply its profile lease to the bookmark UI. Do not enable it until
profile-local file operations have been qualified against concurrent path
replacement and the remaining native acceptance checks have passed.

Bookmarks are stored unencrypted in the active profile directory in
`bookmarks.json`. Each store operation uses the active profile lease. One
manager window is used per browser window. The browser starts one process per
leased profile. Existing metadata exports and backups do not include bookmarks.
To back them up manually, use Export and copy the resulting JSON file from the
profile directory after closing the browser.

Folders are flat, with one level below Bookmarks. Removing a folder also removes
all bookmarks in it after confirmation. IDs are stable, ordering and UTC
timestamps are stored, and each folder accepts one bookmark per normalized URL.
Normalization uses the .NET URI parser, lowercases the scheme and host, removes
default ports and fragments, and retains the path and query. Only HTTP and HTTPS
without credentials are accepted. Internal diagnostics are excluded. Titles
and folder names are trimmed, controls and bidi overrides removed, and limited
to 256 characters. An empty title uses the hostname.

The file accepts at most 10,000 bookmarks, 1,000 folders, 2,048 characters per
URL and 8 MiB of JSON. Version 1 rejects unknown properties and structures
deeper than 16 JSON levels. The JSON export is the same versioned format. Import
and export files must be directly inside the profile directory. The import
preview lists new, duplicate and rejected item counts and rejected item numbers.
Valid entries are added atomically into a new Imported bookmarks folder. The
interchange parser never loads a web page. Netscape HTML is deferred.

A successful save serializes to a new same-directory temporary file, flushes
it, then uses `File.Replace` with `bookmarks.json.bak` when a main file exists,
or a non-overwriting move for the first save. A failed save leaves the current
in-memory document unchanged. A stale editor or another store instance with an
older on-disk generation is rejected; callers must reopen to load current data.
If an interruption
leaves a temporary file, or the main file is missing while a backup exists, the
store blocks writes and asks for manual review. It does not automatically
choose a recovery file. Invalid, oversized or unsupported main files are moved
to a uniquely named `.corrupt` file where possible and are not overwritten.
Review the main, backup and temporary files with the browser closed before
restoring a chosen file.

The store checks the profile path and file attributes for reparse points before
I/O. These path-based checks are not a defense against a concurrent process that
can replace path components between the check and the operation; adversarial
same-user filesystem changes require separate handle-based qualification.

## File-access gate: BLOCKED

### Target threat model (not yet qualified)

The intended boundary protects against accidental path changes and application
mistakes; rejects reparse points already present when opened; and prevents a
different or lower-privileged process from redirecting a bookmark operation
while the validated lease and storage handles are held. None of these are yet
verified for the store. The corresponding positive and negative filesystem
tests are required before claiming protection.

Processes running as the same user are outside the boundary: they can normally
open the same objects and may change permissions, names or pre-open handles.
Administrator and SYSTEM processes are also outside it. The intended supported
scope is fixed local NTFS; UNC/network storage, removable media, FAT, exFAT,
ReFS and cloud placeholders are unsupported until independently qualified.
The proposed anchor is the fixed volume root, with no filesystem ancestors
above that root. The drive mapping and device namespace used to reach it must
still be bound and verified. Remaining races include changes before acquisition,
pre-existing handles, lease-to-directory identity binding, component replacement
while walking, file substitution, publication and recovery. No test has yet
established protection for these races.

`BookmarkDirectoryObservation` is an experimental building block used only by
the tests. It is compiled into the core assembly. Under a live
matching lease it opens a fixed local NTFS directory with `CreateFileW`,
`FILE_FLAG_BACKUP_SEMANTICS`, `FILE_FLAG_OPEN_REPARSE_POINT`,
`FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES`, and read/write sharing without
delete sharing. It reads attributes, volume serial, file ID and final path from
the handle. It is not connected to bookmark storage or application activation.
The observer registers with the already-held lease and closes its handle on
revocation; it still opens after lease acquisition and is not the lease's own
acquisition-time directory binding.

The profile lease currently retains a lock-file handle and pathname; it does not
record the profile directory file ID at lease acquisition. Consequently the
observer cannot establish that a later opened directory is the object initially
leased. There is no handle-validated ancestor chain or established trust anchor,
and the store does not open files relative to the held directory. The intended
anchor is the fixed volume root; anchoring and all ancestor identities still
need implementation and tests. Access control against a different or
lower-privileged identity is unverified. A same-user or administrator process
is outside any claimed protection; an already-open competing handle and
replacement before acquisition remain untested.

| Operation | Existing mechanism | Remaining check-to-use issue |
| --- | --- | --- |
| Profile and ancestor validation | `File.GetAttributes` on pathnames | A checked component can be replaced before later access. |
| Main file load and generation check | Attribute/existence checks then path-based `FileStream` | A different file or directory can be substituted before open. |
| Temporary creation and cleanup | `CreateNew` then pathname deletion | Parent replacement can redirect creation or cleanup. |
| Main publication and backup | Path-based `File.Replace` / `File.Move` | The directory or operands can change after validation. |
| Corrupt-file quarantine and recovery scan | Path-based move and directory enumeration | Replacement can redirect the move or invalidate the scan. |
| Import and export | Path comparison, attributes, and path-based streams and move | Checks do not bind the accessed objects to the validated directory. |

The Windows disposable-profile unit run observed a blocked rename while two
list-access directory handles were held and the held file ID remained stable.
This single observation does not isolate the effect of those handles from the
lease's lock-file handle, test a different identity, or prove ancestor or file
confinement. A previous metadata-only directory handle experiment on a separate
disposable runner allowed deletion and recreation with a different file ID;
omitting delete sharing alone is not a confinement proof. The list-access
comparison blocked the tested leaf delete/rename and held-parent rename, but
did not cover pre-existing handles or the bookmark access path.

The later 40-case Windows sharing observation separated a synthetic exclusive
lock file from a directory handle. With no lock file, an attributes-only handle
without delete sharing allowed the tested profile directory to be renamed or
removed and recreated with a different file ID. A list-access handle without
delete sharing blocked the tested profile rename and removal with sharing error
32. With delete sharing, the tested profile rename, removal and replacement
succeeded even with list access. The lock-file-only case blocked the tested
profile and ancestor renames with error 5; removal reported directory-not-empty
while the lock file existed, so that result does not isolate sharing behavior.
In the ancestor-rename cases a handle on the ancestor with list access and no
delete sharing returned error 32, while the unguarded control succeeded. An
open child-directory handle produced error 5 for the tested ancestor rename,
including with delete sharing. Held identities and final paths were recorded;
this is one filesystem and test process, not a lower-privileged attack, a test
of every ancestor, or an authorization for bookmark storage.

Before activation, bind the lease to directory identity at acquisition; open and
validate the full chain and each file relative to retained handles; qualify
atomic publication and quarantine, including crash recovery; and run the
specified replacement, permission, lower-privilege and independent identity
tests. Unsupported volumes, placeholders and reparse points must be rejected
before writing. Failures leave existing data untouched. No claim of a complete
filesystem protection boundary is made by this prototype.

The star and Ctrl+D apply to the selected tab's committed main-frame URL.
Redirects update the address to the final committed URL. Ctrl+Shift+O opens the
manager. These shortcuts are intercepted by the browser for web content, even
when the page assigns the same keys. No private or ephemeral profile mode is
implemented.
