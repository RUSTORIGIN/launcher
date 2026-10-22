# Signs a launcher release with the OFFLINE release key, then (optionally) publishes it.
#   .\scripts\sign_release.ps1 -Tag v1.3.0 -KeyFile D:\offline\rustorigin-release-key.xml            # sign + upload the .sig
#   .\scripts\sign_release.ps1 -Tag v1.3.0 -KeyFile D:\offline\rustorigin-release-key.xml -Publish   # ... and publish it
#
# The release workflow only creates a DRAFT release (players' launchers only see published ones). This script:
#   1) downloads every asset of the draft and checks each one against its SHA256SUMS.txt (and that nothing
#      unlisted is attached), so what you sign is what CI built;
#   2) signs "rustorigin-launcher-release\n<tag>\n" + SHA256SUMS.txt (UpdateParsing.SignedMessage) and checks
#      the signature with the public key in config\launcher.cfg - signing with the wrong key would leave every
#      player unable to update, so that stops here;
#   3) uploads SHA256SUMS.txt.sig to the release and, with -Publish, publishes it (it becomes "latest").
# Needs the GitHub CLI (gh), signed in with access to the repo. Step 3 is outward-facing: it ships to players.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$KeyFile,
    [string]$Repo = 'RUSTORIGIN/launcher',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { throw "Tag must look like v1.2.3" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "GitHub CLI (gh) not found - winget install GitHub.cli" }
Add-Type -Path (Join-Path $root 'src\UpdateParsing.cs')

# The public key the launchers trust: the one in the committed config (what make_release embeds).
$cfgKey = (Get-Content (Join-Path $root 'config\launcher.cfg') | Where-Object { $_ -match '^\s*UpdatePublicKey\s*=' } | Select-Object -Last 1)
$cfgKey = if ($cfgKey) { ($cfgKey -split '=', 2)[1].Trim() } else { '' }
if ($null -eq [UpdateParsing]::ParsePublicKey($cfgKey)) {   # PowerShell unwraps the Nullable: null = no usable key
    throw "config\launcher.cfg has no valid UpdatePublicKey= line - run scripts\new_signing_key.ps1 first" }

$info = gh release view $Tag -R $Repo --json isDraft,tagName,assets | ConvertFrom-Json
if (-not $info.isDraft) { Write-Warning "$Tag is already published - players may already see it. Signing it anyway." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("rustorigin-sign-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    gh release download $Tag -R $Repo -D $tmp --pattern '*' | Out-Null
    $sumsPath = Join-Path $tmp 'SHA256SUMS.txt'
    if (-not (Test-Path $sumsPath)) { throw "$Tag has no SHA256SUMS.txt" }
    $sums = [IO.File]::ReadAllBytes($sumsPath)

    # 1) every asset matches the sums, and every asset is listed
    $listed = @{}
    foreach ($line in ([Text.Encoding]::UTF8.GetString($sums) -split "`n")) {
        $l = $line.Trim(); if (-not $l) { continue }
        $hash, $name = $l -split '\s+\*?', 2
        $f = Join-Path $tmp $name
        if (-not (Test-Path -LiteralPath $f)) { throw "SHA256SUMS.txt lists $name, which is not attached to $Tag" }
        $actual = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $hash.ToLower()) { throw "$name does not match SHA256SUMS.txt (expected $hash, got $actual)" }
        $listed[$name] = $true
        Write-Host ("  ok  {0}  {1}" -f $hash, $name)
    }
    foreach ($a in Get-ChildItem $tmp -File) {
        if ($a.Name -in 'SHA256SUMS.txt', [UpdateParsing]::SignatureAsset) { continue }
        if (-not $listed.ContainsKey($a.Name)) { throw "$($a.Name) is attached to $Tag but not in SHA256SUMS.txt" }
    }
    if (-not $listed.ContainsKey('RustoriginLauncher.exe')) { throw "SHA256SUMS.txt has no RustoriginLauncher.exe line" }

    # 2) sign, then check against the key the launchers trust
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.FromXmlString((Get-Content -LiteralPath $KeyFile -Raw))
        $msg = [UpdateParsing]::SignedMessage($Tag, $sums)
        $sig = [Convert]::ToBase64String($rsa.SignData($msg, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
    } finally { $rsa.Dispose() }
    if (-not [UpdateParsing]::VerifyRelease($cfgKey, $Tag, $sums, $sig)) {
        throw "The signature does not verify with UpdatePublicKey in config\launcher.cfg - is -KeyFile the right key? Nothing was uploaded."
    }
    $sigPath = Join-Path $tmp ([UpdateParsing]::SignatureAsset)
    [IO.File]::WriteAllText($sigPath, $sig + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "signature verified with the launcher's public key"

    # 3) ship it
    gh release upload $Tag $sigPath -R $Repo --clobber
    Write-Host "uploaded $([UpdateParsing]::SignatureAsset) to $Tag"
    if ($Publish) { gh release edit $Tag -R $Repo --draft=false --latest; Write-Host "$Tag published - launchers will offer it on their next start" }
    else { Write-Host "Draft kept. Publish it when ready:  gh release edit $Tag -R $Repo --draft=false --latest" }
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
