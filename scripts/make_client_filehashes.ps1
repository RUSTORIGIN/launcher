# Prints the AcClientFile= lines for config\launcher.cfg: size + SHA-256 of the game files the launcher's
# anti-cheat session checks at PLAY (docs\CLIENT_ANTICHEAT.md). Run it against the SAME client folder the
# hosted zip was packaged from, whenever the client build changes, and paste the output into launcher.cfg.
#
#   .\scripts\make_client_filehashes.ps1 -ClientDir C:\Rustorigin
#   .\scripts\make_client_filehashes.ps1 -ClientDir <dir> -Files GameAssembly.dll,UnityPlayer.dll
param(
    [Parameter(Mandatory = $true)][string]$ClientDir,
    [string[]]$Files = @('GameAssembly.dll', 'UnityPlayer.dll', 'RustClient_Data\il2cpp_data\Metadata\global-metadata.dat')
)
$ErrorActionPreference = 'Stop'
foreach ($f in $Files) {
    $p = Join-Path $ClientDir $f
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "not found: $p" }
    $hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    'AcClientFile={0}|{1}|{2}' -f $f, (Get-Item -LiteralPath $p).Length, $hash
}
