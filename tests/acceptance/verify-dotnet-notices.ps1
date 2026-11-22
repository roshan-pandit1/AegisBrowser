[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$NoticeDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The runtime component identities were checked against the exact 10.0.15
# NuGet packages containing these notices. This is a version binding, not a
# NuGet signature check or a complete redistribution review.
$files = @(
    @{ root=$PublishDirectory; name='coreclr.dll'; hash='A6988FC77326C245873986446DE4F15218DF929664750D333CB2D42486C071DA' },
    @{ root=$PublishDirectory; name='System.Private.CoreLib.dll'; hash='983F5C8957437AA38FCD239606E60DC2A4A6F551605311591A72E5B285EAE957' },
    @{ root=$PublishDirectory; name='System.Windows.Forms.dll'; hash='E64A7D4CC4988DC78F8231C8B2A2A501D3464F70E461FC72253207B194B3901D' },
    @{ root=$NoticeDirectory; name='DOTNET-RUNTIME-LICENSE.TXT'; hash='D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B' },
    @{ root=$NoticeDirectory; name='DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT'; hash='6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21' },
    @{ root=$NoticeDirectory; name='DOTNET-DESKTOP-LICENSE.TXT'; hash='A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB' }
)
foreach ($item in $files) {
    $file = Join-Path $item.root $item.name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "DOTNET_NOTICES_MISSING: $($item.name)" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $item.hash) {
        throw "DOTNET_NOTICES_MISMATCH: $($item.name)"
    }
}
Write-Output 'DOTNET_NOTICES_PIN_MATCH: PASS (not a complete redistribution review)'
