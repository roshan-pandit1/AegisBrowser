param([string] $TrustDirectory)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($TrustDirectory)) {
    Write-Output 'P403_TRUST_CLEANUP: no trust directory was created'
    exit 0
}
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:CI -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Test CA cleanup is restricted to a disposable GitHub-hosted runner.'
}
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
$target = [IO.Path]::GetFullPath($TrustDirectory)
if (-not $target.StartsWith($runnerTemp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Trust directory must be under RUNNER_TEMP.'
}
if (-not (Test-Path -LiteralPath $target)) {
    Write-Output 'P403_TRUST_CLEANUP: trust directory already absent'
    exit 0
}
foreach ($path in @($runnerTemp, $target)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Linked trust directories are not supported.'
    }
}
$files = @(Get-ChildItem -LiteralPath $target -Force)
if ($files.Count -gt 4) { throw 'Unexpected number of test CA manifests.' }
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    [Security.Cryptography.X509Certificates.StoreName]::Root,
    [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
try {
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor
        [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
    foreach ($file in $files) {
        $nameMatch = [regex]::Match($file.Name, '^p403-ca-([0-9a-f]{32})\.cer$')
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $nameMatch.Success) {
            throw 'Unexpected item in test CA manifest directory.'
        }
        $id = $nameMatch.Groups[1].Value
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($file.FullName)
        try {
            if ($certificate.HasPrivateKey -or $certificate.Subject -cne ('CN=AegisBrowser P403 Disposable Test CA ' + $id)) {
                throw 'Test CA manifest does not match its expected public certificate.'
            }
            $raw = [Convert]::ToBase64String($certificate.RawData)
            $matches = @($store.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false) |
                Where-Object { [Convert]::ToBase64String($_.RawData) -ceq $raw })
            if ($matches.Count -gt 1) { throw 'The test root is not unique.' }
            if ($matches.Count -eq 1) {
                & ./AegisBrowser.BrowserTests/bin/Release/net8.0-windows/win-x64/AegisBrowser.BrowserTests.exe https-privacy-remove-root $file.FullName
                if ($LASTEXITCODE -ne 0) { throw 'The exact test root could not be removed.' }
                $store.Close()
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor
                    [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly)
            }
            $remaining = @($store.Certificates.Find(
                [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $certificate.Thumbprint, $false) |
                Where-Object { [Convert]::ToBase64String($_.RawData) -ceq $raw })
            if ($remaining.Count -ne 0) { throw 'A test root remains in the user trust store.' }
        }
        finally { $certificate.Dispose() }
        Remove-Item -LiteralPath $file.FullName
    }
}
finally { $store.Close(); $store.Dispose() }
if (@(Get-ChildItem -LiteralPath $target -Force).Count -ne 0) { throw 'Trust directory is not empty after cleanup.' }
Remove-Item -LiteralPath $target
Write-Output 'P403_TRUST_CLEANUP: verified public test roots absent and manifests removed'
