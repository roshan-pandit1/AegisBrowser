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

The star and Ctrl+D apply to the selected tab's committed main-frame URL.
Redirects update the address to the final committed URL. Ctrl+Shift+O opens the
manager. These shortcuts are intercepted by the browser for web content, even
when the page assigns the same keys. No private or ephemeral profile mode is
implemented.
