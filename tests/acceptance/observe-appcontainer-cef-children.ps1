param([Parameter(Mandatory = $true)][string] $BrowserTestsExe, [switch] $RunOnDisposableRunner)
$ErrorActionPreference = 'Stop'
if (-not $RunOnDisposableRunner -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Explicit opt-in on a disposable GitHub-hosted runner is required.'
}
function Assert-NoLink([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked path rejected.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required.' }
Assert-NoLink $env:RUNNER_TEMP
$source = [IO.Path]::GetFullPath($BrowserTestsExe)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Synthetic CEF executable not found.' }
Assert-NoLink $source
$sourceDir = Split-Path -Parent $source
foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force -Recurse) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked build output rejected.' }
}
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/AppContainerLaunchResearch.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ContainedCefLauncher.cs')
Add-Type -Path (Join-Path $PSScriptRoot '../../scripts/containment/ProcessIdentityReader.cs')
$testRoot = Join-Path $env:RUNNER_TEMP ('aegis-cef-identities-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $testRoot) { throw 'Synthetic root already exists.' }
$null = New-Item -ItemType Directory -Path $testRoot
Assert-NoLink $testRoot
function Grant-TestAccess([string] $Path, [string] $Sid, [System.Security.AccessControl.FileSystemRights] $Rights) {
    Assert-NoLink $Path
    $acl = Get-Acl -LiteralPath $Path
    $identity = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, $Rights,
        ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Grant-TestRootTraversal([string] $Path, [string] $Sid) {
    Assert-NoLink $Path
    $acl = Get-Acl -LiteralPath $Path
    $identity = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity,
        [System.Security.AccessControl.FileSystemRights]::ReadAndExecute,
        [System.Security.AccessControl.InheritanceFlags]::None,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$runs = @()
try {
    foreach ($role in @('A','B')) {
        $root = Join-Path $testRoot $role
        $fixture = Join-Path $root 'fixture'
        $profile = Join-Path $root 'profile'
        $null = New-Item -ItemType Directory -Path $root
        $null = New-Item -ItemType Directory -Path $fixture
        $null = New-Item -ItemType Directory -Path $profile
        [IO.File]::WriteAllText((Join-Path $profile 'isolation-control'), $role)
        foreach ($entry in Get-ChildItem -LiteralPath $sourceDir -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $fixture -Recurse }
        $name = 'AegisCefResearch.' + [guid]::NewGuid().ToString('N')
        $sid = [AegisContainmentResearch.SuspendedSyntheticProcess]::RegisterSyntheticIdentity($name)
        if ($runs.Count -gt 0 -and $runs[0].Sid -eq $sid) { throw 'Distinct synthetic identity names resolved to the same SID.' }
        Grant-TestRootTraversal $testRoot $sid
        Grant-TestAccess $root $sid ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
        Grant-TestAccess $profile $sid ([System.Security.AccessControl.FileSystemRights]::Modify)
        $exe = Join-Path $fixture 'AegisBrowser.BrowserTests.exe'
        $runs += [pscustomobject]@{ Role = $role; Profile = $profile; Sid = $sid; Name = $name; Child = $null; Executable = $exe }
    }
    if ($runs[0].Sid -eq $runs[1].Sid) { throw 'Distinct synthetic identity names resolved to the same SID.' }
    foreach ($run in $runs) {
        $run.Child = [AegisContainmentResearch.ContainedCefLauncher]::StartObserver($run.Name, $run.Executable, $run.Profile)
        $verified = $run.Child.Verify($run.Sid)
        Write-Output ("OBSERVED: profile={0} suspended pid={1} created={2} SID={3} expectedPath={4}" -f $run.Role, $run.Child.Id, $run.Child.CreationUtc(), $verified, $run.Executable)
    }
    foreach ($run in $runs) { $run.Child.Resume() }
    $deadline = [DateTime]::UtcNow.AddSeconds(55)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (($runs | Where-Object { Test-Path -LiteralPath (Join-Path $_.Profile 'observer-ready') }).Count -eq 2) { break }
        if (($runs | Where-Object { $_.Child.Wait(0) }).Count -gt 0) { break }
        Start-Sleep -Milliseconds 100
    }
    $ready = @($runs | Where-Object { Test-Path -LiteralPath (Join-Path $_.Profile 'observer-ready') })
    if ($ready.Count -ne 2) {
        foreach ($run in $runs) {
            Write-Output ("INCONCLUSIVE: profile={0} ready={1} exited={2}" -f $run.Role, (Test-Path -LiteralPath (Join-Path $run.Profile 'observer-ready')), $run.Child.Wait(0))
            $failure = Join-Path $run.Profile 'failure.txt'
            if (Test-Path -LiteralPath $failure) { Write-Output ('Synthetic failure: ' + (Get-Content -LiteralPath $failure -Raw)) }
        }
    } else {
        Write-Output 'OBSERVED: two distinct AppContainer CEF processes reached the observation barrier concurrently'
        foreach ($run in $runs) { [IO.File]::WriteAllText((Join-Path $run.Profile 'cross-check'), 'check') }
        foreach ($sample in 1..3) {
            foreach ($run in $runs) {
                if ($run.Child.Wait(0)) {
                    Write-Output ("INCONCLUSIVE: profile={0} root exited before sample={1}; descendant snapshot omitted." -f $run.Role, $sample)
                    continue
                }
                $rootInfo = [AegisContainmentResearch.ProcessIdentityReader]::Observe($run.Child.Id)
                if ($rootInfo.Error -or $rootInfo.CreationUtc -ne $run.Child.CreationUtc()) {
                    Write-Output ("INCONCLUSIVE: profile={0} root observation failed or identity changed: {1}" -f $run.Role, $rootInfo.Error)
                    continue
                } else {
                    Write-Output ("OBSERVED: profile={0} root pid={1} created={2} path={3} pathError={4} appcontainer={5} SID={6} intendedSID={7} rawMachine={8:X4}/{9:X4}" -f
                        $run.Role, $rootInfo.Id, $rootInfo.CreationUtc, $rootInfo.ExecutablePath, $rootInfo.PathError, $rootInfo.IsAppContainer, $rootInfo.PackageSid, $run.Sid, $rootInfo.ProcessMachine, $rootInfo.NativeMachine)
                    if (-not $rootInfo.IsAppContainer -or $rootInfo.PackageSid -ne $run.Sid) { Write-Output ("OBSERVED: profile={0} root token differs from expected identity; containment is unverified." -f $run.Role) }
                }
                $children = @([AegisContainmentResearch.ProcessIdentityReader]::SnapshotDescendants($run.Child.Id))
                if ($children.Count -eq 0) { Write-Output ("INCONCLUSIVE: profile={0} sample={1} no child observed" -f $run.Role, $sample) }
                foreach ($link in $children) {
                    $info = [AegisContainmentResearch.ProcessIdentityReader]::Observe($link.Id)
                    if ($info.Error) {
                        Write-Output ("INCONCLUSIVE: profile={0} sample={1} pid={2} parent={3} image={4} tokenError={5}" -f $run.Role, $sample, $link.Id, $link.ParentId, $link.ImageName, $info.Error)
                        continue
                    }
                    if ([DateTime]::Parse($info.CreationUtc) -lt [DateTime]::Parse($run.Child.CreationUtc())) {
                        Write-Output ("INCONCLUSIVE: profile={0} pid={1} predates the root; PID association may be stale" -f $run.Role, $link.Id)
                        continue
                    }
                    $role = 'unknown'
                    try {
                        $cim = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $link.Id) -ErrorAction Stop
                        $created = [DateTime]::Parse($info.CreationUtc).ToUniversalTime()
                        if ($null -eq $cim -or $cim.ParentProcessId -ne $link.ParentId -or $null -eq $cim.CreationDate -or
                            [Math]::Abs(($cim.CreationDate.ToUniversalTime() - $created).TotalMilliseconds) -ge 1) {
                            Write-Output ("INCONCLUSIVE: role identity could not be matched for pid={0}" -f $link.Id)
                        } else {
                            if ($cim.CommandLine -match '(?:^|\s)--type=([^\s]+)') { $role = $Matches[1] }
                            if ($cim.CommandLine -match '(?:^|\s)--utility-sub-type=([^\s]+)') { $role += ':' + $Matches[1] }
                        }
                    } catch { Write-Output ("INCONCLUSIVE: role unavailable for pid={0}" -f $link.Id) }
                    $relation = if ($info.IsAppContainer -and $info.PackageSid -eq $run.Sid) { 'matches-root' } else { 'different-or-absent' }
                    Write-Output ("OBSERVED: profile={0} sample={1} pid={2} parent={3} created={4} image={5} path={6} pathError={7} provisionalRole={8} appcontainer={9} SID={10} intendedSID={11} relation={12} rawMachine={13:X4}/{14:X4}" -f
                        $run.Role, $sample, $link.Id, $link.ParentId, $info.CreationUtc, $link.ImageName, $info.ExecutablePath, $info.PathError, $role,
                        $info.IsAppContainer, $info.PackageSid, $run.Sid, $relation, $info.ProcessMachine, $info.NativeMachine)
                    if ($relation -ne 'matches-root' -and $role -match '(?i)network') {
                        Write-Output ("OBSERVED: network-role child pid={0} does not match its root SID; package-SID-only containment is unverified." -f $link.Id)
                    } elseif ($relation -ne 'matches-root') {
                        Write-Output ("INCONCLUSIVE: child pid={0} has a different or absent SID; its network role and enforceable boundary require investigation." -f $link.Id)
                    }
                }
            }
            if ($sample -lt 3) { Start-Sleep -Milliseconds 500 }
        }
        $crossDeadline = [DateTime]::UtcNow.AddSeconds(15)
        foreach ($run in $runs) {
            $names = @('cross-access-allowed','cross-access-denied','cross-access-inconclusive')
            while ([DateTime]::UtcNow -lt $crossDeadline -and @($names | Where-Object { Test-Path -LiteralPath (Join-Path $run.Profile $_) }).Count -eq 0) {
                Start-Sleep -Milliseconds 100
            }
            $own = Test-Path -LiteralPath (Join-Path $run.Profile 'own-control-read')
            $state = @($names | Where-Object { Test-Path -LiteralPath (Join-Path $run.Profile $_) }) -join ','
            if (-not $state) { $state = 'no-result-inconclusive' }
            Write-Output ("OBSERVED: profile={0} ownControlReadable={1} otherProfileControl={2}" -f $run.Role, $own, $state)
            if ($state -match 'cross-access-allowed') { Write-Output ("OBSERVED FAIL: profile={0} read the other synthetic profile's control file." -f $run.Role) }
            elseif (-not $own) { Write-Output ("INCONCLUSIVE: profile={0} could not demonstrate its own control-file read." -f $run.Role) }
        }
        Write-Output 'INCONCLUSIVE: snapshots can miss transient, reparented or brokered subprocesses and do not establish network containment.'
    }
    foreach ($run in $runs) { [IO.File]::WriteAllText((Join-Path $run.Profile 'observer-release'), 'release') }
    foreach ($run in $runs) {
        if (-not $run.Child.Wait(90000)) {
            Write-Output ("INCONCLUSIVE: profile={0} timed out; terminating its owned synthetic process." -f $run.Role)
            $run.Child.StopTimedOutTest()
            continue
        }
        $code = $run.Child.ExitCode()
        $shutdown = Test-Path -LiteralPath (Join-Path $run.Profile 'shutdown')
        Write-Output ("OBSERVED: profile={0} exit={1} nativeShutdownMarker={2}" -f $run.Role, $code, $shutdown)
        if ($code -ne 0 -or -not $shutdown) { Write-Output ("OBSERVED FAIL: profile={0} did not complete normal shutdown." -f $run.Role) }
    }
}
finally {
    $cleanupErrors = @()
    foreach ($run in $runs) {
        try { if (Test-Path -LiteralPath $run.Profile) { [IO.File]::WriteAllText((Join-Path $run.Profile 'observer-release'), 'release') } }
        catch { $cleanupErrors += ("release profile {0}: {1}" -f $run.Role, $_.Exception.Message) }
        try { if ($null -ne $run.Child) { $run.Child.Dispose() } }
        catch { $cleanupErrors += ("dispose profile {0}: {1}" -f $run.Role, $_.Exception.Message) }
    }
    Write-Output ("OBSERVATION: synthetic identities and data retained until disposable runner destruction: {0}" -f $testRoot)
    if ($cleanupErrors.Count) { throw ($cleanupErrors -join '; ') }
}
