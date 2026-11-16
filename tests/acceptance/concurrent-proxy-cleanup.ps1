# Only call this with a Process object created and retained by this test.
# Accessing Handle before the process exits retains its original process object;
# it prevents this cleanup from selecting an unrelated PID by name or discovery.
function Stop-TestOwnedProxyProcess {
    param(
        [Parameter(Mandatory = $true)] [System.Diagnostics.Process] $Process,
        [ValidateRange(1, 30000)] [int] $WaitMilliseconds = 5000
    )

    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            try { $Process.Kill() }
            catch [System.InvalidOperationException] {
                # The owned process can exit between HasExited and Kill.
                $Process.Refresh()
                if (-not $Process.HasExited) { throw }
            }
        }
        return $Process.WaitForExit($WaitMilliseconds)
    }
    catch {
        return $false
    }
}
