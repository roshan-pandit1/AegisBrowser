# Download staging: proposed boundary and activation gate

Status: BLOCKED. The production browser still uses `DenyDownloads`. This is a
design and test boundary, not evidence that staged downloads are safe.

## Intended filesystem scope

The candidate scope is fixed, local NTFS storage. Both the profile-owned
staging area and the approved destination must be on the same volume for an
atomic same-volume publication. A destination on a different volume is
rejected; silently copying across volumes is not permitted. UNC paths, network
volumes, removable media, FAT, exFAT, ReFS and cloud-sync placeholders are
unsupported and must be rejected before CEF is continued. No destination is
currently supported by the production browser.

The staging directory must be created inside the exclusively leased profile,
one uniquely named directory per download, with a per-download ownership
marker and an explicitly restricted DACL. Its identity and each ancestor's
identity must be obtained through native handles. The directory handles and
their sharing modes must remain in effect throughout the transfer. A file or
directory name, GUID or marker alone is not proof of ownership. On failure,
only an object whose ownership and identity can still be established may be
removed; unknown leftovers must be preserved. A staged file must never be
automatically published on a later startup.

## Adversaries and limits

The design must resist attempts by a different-user or lower-privileged local
process to replace or redirect staging and destination objects, subject to the
verified ACLs and handle operations. It must also test same-user competing
processes. A same-user process can generally exercise the user's authority,
change files and ACLs it owns, pre-open handles, or interfere before the
browser acquires its handles. Neither a private ACL nor a held handle is a
general boundary against that process. Tests must state their handle-open and
privilege ordering and identify any remaining races; uncertain identity or
access checks fail closed.

The target path handed to CEF must be inside the app-created staging directory,
never the approved destination. CEF's pinned `GenerateFilename` uses the
provided path if its directory exists or can be created. If directory creation
fails it clears that path; an empty path then falls back to `DIR_TEMP`, and if
that lookup fails to the suggested name in the current working directory.
`ChooseDownloadPath` without a CEF dialog passes the resulting path to
`RunDownloadTargetCallback`, which assigns it to both the target and
intermediate paths. A nonempty callback argument therefore does not by itself
rule out an unintended fallback.

The proposed publication must use a handle-relative operation with no-replace
semantics, verify file identity, length and SHA-256 before and after, and retain
an unpublishable staged file with an explicit failure result. The path-only
`DownloadDestinations.IsAvailable` helper is an advisory preflight; it cannot
reserve a path, validate object identity or establish these properties.

## Evidence required before activation

Use an authorized disposable Windows VM to observe actual writes independently
of CEF callbacks. Cover destination and staging replacement, reparse points,
ACL changes, removal and rename attempts, full disk, concurrent transfers,
interruption and abnormal termination. Compare the controlled staging,
destination, process temporary, working and profile directories. Independently
verify handle identities and the final published hash. Test cancellation and
its terminal callback before closing the originating browser, including popup,
initialization and shutdown races. Passing individual fixture checks does not
establish the full activation gate.
