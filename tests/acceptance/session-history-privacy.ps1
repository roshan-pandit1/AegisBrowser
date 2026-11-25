param([Parameter(Mandatory=$true)][string]$BrowserTestsExe)
$ErrorActionPreference = 'Stop'
$scanRoot = Join-Path ([IO.Path]::GetTempPath()) ('aegis-history-privacy-' + [guid]::NewGuid().ToString('N'))
$tempDir = Join-Path $scanRoot 'temp'
$profileDir = Join-Path $scanRoot 'profile'
New-Item -ItemType Directory -Path $tempDir,$profileDir | Out-Null
$before = @(Get-ChildItem -LiteralPath $scanRoot -File -Recurse | ForEach-Object FullName)
$marker = [guid]::NewGuid().ToString('N')
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
try {
    $env:TEMP = $tempDir; $env:TMP = $tempDir
    foreach ($phase in @('history-privacy-write', 'history-privacy-read')) {
        $output = & $BrowserTestsExe $phase $profileDir $marker 2>&1
        $code = $LASTEXITCODE
        $text = $output -join "`n"
        if ($text.Contains($marker)) { throw 'The canary appeared in fixture stdout or stderr.' }
        if ($code -ne 0 -or $text -match 'Check failed:|FATAL:|ASSERTION FAILED') { throw "Privacy phase failed: $phase; exit code $code." }
        $output | Where-Object { $_ -match '^PASS:' }
    }
    $files = @(Get-ChildItem -LiteralPath $scanRoot -File -Recurse)
    $chromiumHits = 0; $unclassifiedHits = 0
    foreach ($file in $files) {
        if ($file.Length -gt 128MB) { throw 'A test file exceeds the bounded scan limit.' }
        $data = [IO.File]::ReadAllBytes($file.FullName)
        if ([Text.Encoding]::UTF8.GetString($data).Contains($marker) -or [Text.Encoding]::Unicode.GetString($data).Contains($marker)) {
            $relative = if ($file.FullName.StartsWith($profileDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $file.FullName.Substring($profileDir.Length + 1)
            } else { 'isolated-temp' + [IO.Path]::DirectorySeparatorChar + $file.Name }
            if ($relative -match '^(Default[\\/]History(?:-wal|-shm)?|Default[\\/]Cache[\\/]Cache_Data[\\/].+|segmentation_platform[\\/]ukm_db(?:-wal|-shm)?)$') {
                $chromiumHits++
                Write-Output ('CHROMIUM_CANARY_FILE: ' + $relative)
            } else {
                $unclassifiedHits++
                Write-Output ('UNCLASSIFIED_CANARY_FILE: ' + $file.Name)
            }
        }
    }
    $added = @($files | Where-Object { $_.FullName -notin $before })
    $featureFiles = @($added | Where-Object { $_.Name -match '(?i)session[-_ ]?history' })
    Write-Output "SCAN_RESULT files=$($files.Count) added=$($added.Count) chromium_matches=$chromiumHits unclassified_matches=$unclassifiedHits"
    Write-Output "Disposable scan root: $scanRoot"
    if ($unclassifiedHits -ne 0) { throw 'A canary was found in an unclassified file.' }
    if ($featureFiles.Count -ne 0) { throw 'A session-history-named file appeared in the fixture.' }
    Write-Output 'PASS: no session-history-named file appeared in the fixture'
    Write-Output 'PASS: no canary in fixture stdout, stderr, or unclassified scanned files'
} finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
