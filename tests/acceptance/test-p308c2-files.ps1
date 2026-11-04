$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'p308c2-file-lib.ps1')
$root = Join-Path $env:TEMP ('aegis-p308c2-files-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
$path = Join-Path $root 'evidence.bin'
[IO.File]::WriteAllBytes($path, [byte[]](1,2,3))
$checks = 0
$result = Read-P308c2EvidenceFile -Path $path -MaximumBytes 3
if ($result.Length -ne 3 -or $result[2] -ne 3) { throw 'Incorrect read' }; $checks++
foreach ($case in @(
    @{ path=$path; max=2 }, @{ path=(Join-Path $root 'missing'); max=10 },
    @{ path=$root; max=10 }, @{ path='relative.bin'; max=10 },
    @{ path=($path + ':stream'); max=10 }
)) {
    $rejected=$false
    try { Read-P308c2EvidenceFile -Path $case.path -MaximumBytes $case.max | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Unsafe path accepted' }; $checks++
}
$signaturePath = Join-Path $root 'signature.bin'
[IO.File]::WriteAllBytes($signaturePath, [byte[]](4,5))
$record = Import-P308c2FileRecord -PayloadPath $path -SignaturePath $signaturePath
if ($record.Payload.Length -ne 3 -or $record.Signature.Length -ne 2) { throw 'Incorrect imported pair' }; $checks++
Write-Output "PASS: $checks bounded file-ingestion checks; retained synthetic files at $root"
