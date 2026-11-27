$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../../scripts/archive-entry-policy.ps1')

# Pure decision vectors: no filesystem writes, process creation or networking.
$valid = @(
    @{ Name = 'AegisBrowser/resources.pak'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/locales/'; IsDirectory = $true },
    @{ Name = ('AegisBrowser/caf' + [char]0x00E9 + '.txt'); IsDirectory = $false },
    @{ Name = 'AegisBrowser/.config'; IsDirectory = $false }
)
$invalid = @(
    @{ Name = ''; IsDirectory = $false },
    @{ Name = '/absolute'; IsDirectory = $false },
    @{ Name = '//server/share'; IsDirectory = $false },
    @{ Name = 'C:relative'; IsDirectory = $false },
    @{ Name = 'C:/absolute'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/../escape'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/./entry'; IsDirectory = $false },
    @{ Name = 'AegisBrowser//entry'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/name:stream'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/name.'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/name '; IsDirectory = $false },
    @{ Name = 'AegisBrowser/CON.txt'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/lpt1'; IsDirectory = $false },
    @{ Name = ('AegisBrowser/COM' + [char]0x00B9 + '.txt'); IsDirectory = $false },
    @{ Name = ('AegisBrowser/' + [char]1 + 'name'); IsDirectory = $false },
    @{ Name = 'AegisBrowser\entry'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/entry/'; IsDirectory = $false },
    @{ Name = 'AegisBrowser/entry'; IsDirectory = $true }
)

$count = 0
foreach ($item in $valid) {
    Assert-AegisArchiveEntry -EntryName $item.Name -IsDirectory $item.IsDirectory
    $count++
}
foreach ($item in $invalid) {
    $refused = $false
    try { Assert-AegisArchiveEntry -EntryName $item.Name -IsDirectory $item.IsDirectory }
    catch {
        if ($_.Exception.Message -cne 'PACKAGE_ZIP_ENTRY_INVALID') { throw }
        $refused = $true
    }
    if (-not $refused) { throw 'An unsafe archive-entry vector was accepted.' }
    $count++
}
Write-Output "PASS [SYNTHETIC]: $count pure archive-entry policy vectors"
