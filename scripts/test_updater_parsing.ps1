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
$lookalike = "1111111111111111111111111111111111111111111111111111111111111111  evil-RustoriginLauncher.exe`n" +
             "716fb0f17748da6eb90ca46c6b184dc9ad567c871008098ded0eb7579a6519d8 *RustoriginLauncher.exe`r`n"
Check "HashFromSums exact name, not a suffix" ([UpdateParsing]::HashFromSums($lookalike,'RustoriginLauncher.exe') -eq '716fb0f17748da6eb90ca46c6b184dc9ad567c871008098ded0eb7579a6519d8')
Check "HashFromSums non-hex -> null" ($null -eq [UpdateParsing]::HashFromSums(("z" * 64) + "  RustoriginLauncher.exe",'RustoriginLauncher.exe'))
# ---- release signature (UpdateParsing.VerifyRelease) ----
function New-Key([int]$bits) { New-Object Security.Cryptography.RSACryptoServiceProvider $bits }
function PubLine($rsa) { $p = $rsa.ExportParameters($false); 'rsa:' + [Convert]::ToBase64String($p.Modulus) + ':' + [Convert]::ToBase64String($p.Exponent) }
function SignWith($rsa, $tag, [byte[]]$data) { [Convert]::ToBase64String($rsa.SignData([UpdateParsing]::SignedMessage($tag, $data), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) }
$key = New-Key 3072; $other = New-Key 3072; $pub = PubLine $key
$sumsBytes = [Text.Encoding]::UTF8.GetBytes($sums)
$sig = SignWith $key 'v1.2.0' $sumsBytes
Check "signature: valid release verifies"      ([UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $sumsBytes, $sig))
$tampered = [byte[]]$sumsBytes.Clone(); $tampered[3] = $tampered[3] -bxor 1
Check "signature: changed sums refused"        (-not [UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $tampered, $sig))
Check "signature: other tag refused (replay)"  (-not [UpdateParsing]::VerifyRelease($pub, 'v9.9.9', $sumsBytes, $sig))
Check "signature: other key refused"           (-not [UpdateParsing]::VerifyRelease((PubLine $other), 'v1.2.0', $sumsBytes, $sig))
Check "signature: signed by other key refused" (-not [UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $sumsBytes, (SignWith $other 'v1.2.0' $sumsBytes)))
Check "signature: garbage refused, no throw"   (-not [UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $sumsBytes, 'not base64!'))
Check "signature: empty refused"               (-not [UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $sumsBytes, ''))
Check "signature: no key refused"              (-not [UpdateParsing]::VerifyRelease('', 'v1.2.0', $sumsBytes, $sig))
$weak = New-Key 2048
Check "key: 2048-bit key rejected"             ($null -eq [UpdateParsing]::ParsePublicKey((PubLine $weak)))
Check "key: 3072-bit key accepted"             ($null -ne [UpdateParsing]::ParsePublicKey($pub))
Check "key: malformed rejected"                ($null -eq [UpdateParsing]::ParsePublicKey('rsa:abc'))
Check "signature: trailing newline in .sig ok" ([UpdateParsing]::VerifyRelease($pub, 'v1.2.0', $sumsBytes, $sig + "`n"))

Check "IsNewer newer -> true"     ([UpdateParsing]::IsNewer('1.0.0.0','v1.2.0'))
Check "IsNewer equal -> false"    (-not [UpdateParsing]::IsNewer('1.2.0.0','v1.2.0'))
Check "IsNewer older -> false"    (-not [UpdateParsing]::IsNewer('2.0.0.0','v1.9.9'))
Check "IsNewer patch bump"        ([UpdateParsing]::IsNewer('1.0.0.0','v1.0.1'))
Check "IsNewer bad tag -> false"  (-not [UpdateParsing]::IsNewer('1.0.0.0','banana'))
Check "IsNewer null tag -> false" (-not [UpdateParsing]::IsNewer('1.0.0.0',$null))

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall updater-parsing tests passed"; exit 0
