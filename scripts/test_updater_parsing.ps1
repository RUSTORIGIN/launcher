# Unit tests for the launcher self-update parsing (src\UpdateParsing.cs). Compiles the real source
# with Add-Type and asserts against sample GitHub data. Run: .\scripts\test_updater_parsing.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $repo 'src\UpdateParsing.cs')

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }

$base = 'https://github.com/RUSTORIGIN/RustoriginLauncher/releases/download/v1.2.0'
$json = '{"tag_name":"v1.2.0","name":"Rustorigin Launcher 1.2.0","assets":[' +
        '{"name":"RustoriginLauncher.exe","browser_download_url":"' + $base + '/RustoriginLauncher.exe"},' +
        '{"name":"SHA256SUMS.txt","browser_download_url":"' + $base + '/SHA256SUMS.txt"}]}'
$sums = "716fb0f17748da6eb90ca46c6b184dc9ad567c871008098ded0eb7579a6519d8  RustoriginLauncher.exe`n" +
        "0000000000000000000000000000000000000000000000000000000000000000  other.bin`n"

Check "JsonStr tag_name"          ([UpdateParsing]::JsonStr($json,'tag_name') -eq 'v1.2.0')
Check "JsonStr missing -> null"   ($null -eq [UpdateParsing]::JsonStr($json,'nope'))
Check "AssetUrl RustoriginLauncher.exe"   ([UpdateParsing]::AssetUrl($json,'RustoriginLauncher.exe') -eq "$base/RustoriginLauncher.exe")
Check "AssetUrl SHA256SUMS.txt"   ([UpdateParsing]::AssetUrl($json,'SHA256SUMS.txt') -eq "$base/SHA256SUMS.txt")
Check "AssetUrl missing -> null"  ($null -eq [UpdateParsing]::AssetUrl($json,'nope.exe'))
Check "HashFromSums exe"          ([UpdateParsing]::HashFromSums($sums,'RustoriginLauncher.exe') -eq '716fb0f17748da6eb90ca46c6b184dc9ad567c871008098ded0eb7579a6519d8')
Check "HashFromSums missing null" ($null -eq [UpdateParsing]::HashFromSums($sums,'zzz.exe'))
Check "IsNewer newer -> true"     ([UpdateParsing]::IsNewer('1.0.0.0','v1.2.0'))
Check "IsNewer equal -> false"    (-not [UpdateParsing]::IsNewer('1.2.0.0','v1.2.0'))
Check "IsNewer older -> false"    (-not [UpdateParsing]::IsNewer('2.0.0.0','v1.9.9'))
Check "IsNewer patch bump"        ([UpdateParsing]::IsNewer('1.0.0.0','v1.0.1'))
Check "IsNewer bad tag -> false"  (-not [UpdateParsing]::IsNewer('1.0.0.0','banana'))
Check "IsNewer null tag -> false" (-not [UpdateParsing]::IsNewer('1.0.0.0',$null))

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall updater-parsing tests passed"; exit 0
