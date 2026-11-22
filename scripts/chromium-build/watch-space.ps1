[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [ValidateRange(1, 4096)][int]$MinimumFreeGiB = 200,
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 60,
    [ValidateRange(1, 10000)][int]$Samples = 60
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $BuildRoot -PathType Container)) {
    throw 'SPACE_MONITOR_FAILED: build root must already exist.'
}
$fullPath = (Resolve-Path -LiteralPath $BuildRoot).Path
$driveRoot = [IO.Path]::GetPathRoot($fullPath)
if ($driveRoot -notmatch '^[A-Za-z]:\\$') { throw 'SPACE_MONITOR_FAILED: local drive required.' }
$drive = [IO.DriveInfo]::new($driveRoot)
if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -ne 'NTFS') {
    throw 'SPACE_MONITOR_FAILED: fixed NTFS drive required.'
}
for ($sample = 1; $sample -le $Samples; $sample++) {
    $free = $drive.AvailableFreeSpace
    [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); free_bytes = $free;
        threshold_bytes = ([long]$MinimumFreeGiB * 1GB); sample = $sample } |
        ConvertTo-Json -Compress
    if ($free -lt ([long]$MinimumFreeGiB * 1GB)) {
        Write-Error 'SPACE_MONITOR_LOW: operator action required; the build has not been stopped.' -ErrorAction Continue
        exit 2
    }
    if ($sample -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
