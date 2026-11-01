param([switch] $RegisterOnDisposableRunner)

$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ProcessIdentityReader.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/AppContainerLaunchResearch.cs')
$names = @(('AegisResearch.' + [guid]::NewGuid().ToString('N')), ('AegisResearch.' + [guid]::NewGuid().ToString('N')))
$first = [AegisContainmentResearch.SuspendedSyntheticProcess]::DeriveSyntheticSid($names[0])
$repeat = [AegisContainmentResearch.SuspendedSyntheticProcess]::DeriveSyntheticSid($names[0])
$second = [AegisContainmentResearch.SuspendedSyntheticProcess]::DeriveSyntheticSid($names[1])
if ($first -ne $repeat -or $first -eq $second) { throw 'Synthetic identities did not derive stable, distinct SIDs.' }
Write-Output 'PASS: two synthetic identity names derive stable, distinct AppContainer SIDs'

if ($RegisterOnDisposableRunner) {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'Synthetic AppContainer registration is restricted to a disposable GitHub-hosted runner.'
    }
    foreach ($index in 0..1) {
        $registered = [AegisContainmentResearch.SuspendedSyntheticProcess]::RegisterSyntheticIdentity($names[$index])
        if ($registered -ne @($first, $second)[$index]) { throw 'Registered and derived synthetic SIDs differ.' }
        Write-Output ("OBSERVED: registered disposable per-user test identity {0}" -f $names[$index])
    }
}

$children = @()
try {
    try {
        foreach ($name in $names) { $children += [AegisContainmentResearch.SuspendedSyntheticProcess]::Start($name) }
    }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        if ($cause -isnot [System.ComponentModel.Win32Exception]) { throw }
        Write-Output ("INCONCLUSIVE: suspended AppContainer control process creation failed: {0}; Win32 error {1}" -f $cause.Message, $cause.NativeErrorCode)
        return
    }
    foreach ($child in $children) {
        $observed = [AegisContainmentResearch.ProcessIdentityReader]::Observe($child.Id)
        if ($observed.Error) {
            Write-Output ("INCONCLUSIVE: suspended control token could not be queried: {0}" -f $observed.Error)
            return
        }
        if (-not $observed.IsAppContainer -or $observed.PackageSid -ne $child.ExpectedPackageSid) {
            throw 'Suspended control process did not receive the requested AppContainer token.'
        }
        Write-Output ("OBSERVED: suspended synthetic process pid={0} has its requested AppContainer SID" -f $child.Id)
    }
    Write-Output 'PASS: two suspended synthetic processes received distinct requested AppContainer identities before resume'
    foreach ($child in $children) {
        $code = $child.ResumeAndWait()
        if ($code -ne 0) { Write-Output ("INCONCLUSIVE: synthetic command exit code was {0}" -f $code) }
        else { Write-Output 'PASS: synthetic command exited after resume' }
    }
    Write-Output 'INCONCLUSIVE: this does not test CEF, child-process inheritance, WFP or network containment.'
}
finally {
    foreach ($child in $children) { $child.Dispose() }
}
