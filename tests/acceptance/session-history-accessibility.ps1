param(
    [Parameter(Mandatory=$true)][long]$WindowHandle,
    [Parameter(Mandatory=$true)][int]$OwnerProcessId,
    [Parameter(Mandatory=$true)][ValidateSet('active','paused')][string]$State,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class AegisHistoryWindowObservation { [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags); [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window); [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags); [DllImport("shcore.dll")] public static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale); }'

$handle = [IntPtr]::new($WindowHandle)
$window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
if ($null -eq $window -or $window.Current.ProcessId -ne $OwnerProcessId -or $window.Current.Name -ne 'Session history') {
    throw 'UI Automation could not identify the fixture history window.'
}
$names = if ($State -eq 'active') {
    @('Search session history', 'Visited pages', 'Open', 'Open in new tab', 'Delete selected', 'Clear all',
      'Pause recording', 'Recording: active', 'Session history privacy information')
} else { @('Resume recording', 'Recording: paused') }
foreach ($name in $names) {
    $query = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $query)
    if ($null -eq $element) { throw "A required history accessibility element is absent: $name" }
    if (-not $element.Current.IsEnabled -or $element.Current.IsOffscreen) {
        throw "A required history accessibility element is disabled or offscreen: $name"
    }
}
Write-Output ("PASS: UI Automation observed {0} named, enabled and onscreen history controls in the {1} state" -f $names.Count, $State)

if ($State -eq 'active') {
    $monitor = [AegisHistoryWindowObservation]::MonitorFromWindow($handle, 2)
    $scale = 0
    $result = [AegisHistoryWindowObservation]::GetScaleFactorForMonitor($monitor, [ref]$scale)
    if ($result -ne 0 -or $scale -le 0) { throw 'Could not observe the monitor scaling factor.' }
    $dpi = [AegisHistoryWindowObservation]::GetDpiForWindow($handle)
    $bounds = $window.Current.BoundingRectangle
    $width = [int][Math]::Ceiling($bounds.Width); $height = [int][Math]::Ceiling($bounds.Height)
    if ($width -le 0 -or $height -le 0) { throw 'The history window has no observable screen bounds.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($EvidenceDirectory)) | Out-Null
    $imagePath = Join-Path ([IO.Path]::GetFullPath($EvidenceDirectory)) ('history-scale-' + $scale + '-' + [guid]::NewGuid().ToString('N') + '.png')
    if (Test-Path -LiteralPath $imagePath) { throw 'Refusing to replace an existing screenshot.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try { if (-not [AegisHistoryWindowObservation]::PrintWindow($handle, $dc, 2)) { throw 'PrintWindow could not capture the fixture window.' } }
            finally { $graphics.ReleaseHdc($dc) }
        } finally { $graphics.Dispose() }
        $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    Write-Output "HISTORY_ACCESSIBILITY_EVIDENCE: monitor_scale=$scale window_dpi=$dpi width=$width height=$height"
    Write-Output "HISTORY_SCREENSHOT: $imagePath"
}