# Which files of a client folder SHIP to players. Dot-sourced by package_client.ps1 (full zip) and
# make_client_patch.ps1 (delta pack) so both always agree on the file set.
#
# Excluded, even if the game recreated them after a test launch:
#   temp\  (video cache)     maps\  (map cache, fetched from the server)
#   cfg\*  except keys_default.cfg  (personal keybinds/settings)
#   EasyAntiCheat\ and the root Rust.exe (the EAC bootstrapper) - the launcher starts RustClient.exe
#     directly and never runs EAC, so a Steam depot folder can be packaged as-is
#   the launcher's own files, so an INSTALLED client folder can be used as a source too:
#     RustoriginLauncher.exe, Uninstall.exe, .rustorigin-installed, _download\, _update\
#   *.bak *.log *.dmp *.tmp *.old *.orig *.py *.vdf *.bat *.before*  Thumbs.db desktop.ini
#
# $rel is the path relative to the client folder, with '/' separators.
function Test-ClientExcluded([string]$rel) {
    $r = $rel.ToLowerInvariant()
    if ($r.StartsWith('temp/') -or $r.StartsWith('maps/')) { return $true }
    if ($r.StartsWith('cfg/') -and $r -ne 'cfg/keys_default.cfg') { return $true }
    if ($r.StartsWith('easyanticheat/') -or $r -eq 'rust.exe') { return $true }
    if ($r.StartsWith('_download/') -or $r.StartsWith('_update/')) { return $true }
    if ($r -in 'rustoriginlauncher.exe','uninstall.exe','.rustorigin-installed') { return $true }
    $name = [System.IO.Path]::GetFileName($r)
    if ($name -match '\.(bak|log|dmp|tmp|old|orig|py|vdf|bat)$') { return $true }
    if ($name -like '*.before*') { return $true }
    if ($name -in 'thumbs.db','desktop.ini','.ds_store') { return $true }
    return $false
}

# The shipping files of a client folder, as '/'-separated relative paths in a stable order.
function Get-ClientFiles([string]$dir) {
    $dir = (Resolve-Path -LiteralPath $dir).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $dir -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($dir.Length + 1).Replace('\', '/') } |
        Where-Object { -not (Test-ClientExcluded $_) } |
        Sort-Object { $_.ToLowerInvariant() }
}
