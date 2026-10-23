# Prints the AcClientFile= lines for config\launcher.cfg: size + SHA-256 of the game files the launcher's
# anti-cheat session checks at PLAY (docs\CLIENT_ANTICHEAT.md). Run it against the SAME client folder the
# hosted zip was packaged from, whenever the client build changes, and paste the output into launcher.cfg.
#
# By default it lists every .dll and .exe in the client folder (the build's code files) plus the il2cpp
# metadata. The loaded-module signal (docs\AC_MODULE_SIGNAL.md) treats a module in the game folder that is
# NOT on this list as foreign (client:extra-module), so the list has to be complete; it only switches that
# check on when the list includes the game's own exe (RustClient.exe). The launcher's own exe and the
# installer's uninstaller sit in the install folder on players' PCs, but are not part of the client build
# and are never loaded into the game, so they are left out.
#
#   .\scripts\make_client_filehashes.ps1 -ClientDir C:\Rustorigin
#   .\scripts\make_client_filehashes.ps1 -ClientDir <dir> -Files GameAssembly.dll,UnityPlayer.dll
param(
    [Parameter(Mandatory = $true)][string]$ClientDir,
    [string[]]$Files,
    [string[]]$Exclude = @('RustoriginLauncher.exe', 'Uninstall.exe')
)
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath $ClientDir).FullName.TrimEnd('\')
if (-not $Files) {
    $Files = @(Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { ($_.Extension -eq '.dll' -or $_.Extension -eq '.exe') -and $Exclude -notcontains $_.Name } |
        ForEach-Object { $_.FullName.Substring($root.Length + 1) } | Sort-Object)
    $Files += 'RustClient_Data\il2cpp_data\Metadata\global-metadata.dat'
}
foreach ($f in $Files) {
    $p = Join-Path $root $f
    if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "not found: $p" }
    $hash = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    'AcClientFile={0}|{1}|{2}' -f $f, (Get-Item -LiteralPath $p).Length, $hash
}
