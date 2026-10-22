# Creates the launcher's release signing key (RSA-3072). Run ONCE, on a machine you trust:
#   .\scripts\new_signing_key.ps1 -OutFile D:\offline\rustorigin-release-key.xml
# The PRIVATE key goes to -OutFile (never inside this repo); the PUBLIC key is printed as the
# UpdatePublicKey= line for config\launcher.cfg. Every release's SHA256SUMS.txt is then signed with
# scripts\sign_release.ps1, and launchers built with that line only install updates signed by this key.
#
# Keep the private key OFFLINE and backed up (e.g. an encrypted USB stick + a second copy):
#   - lost   -> you can't ship launcher updates any more; players must reinstall a launcher with a new key
#   - leaked -> whoever has it can sign a malicious update for every player; build a launcher with a new
#               key and ship it (signed with the OLD key one last time) as soon as possible
param([Parameter(Mandatory)][string]$OutFile)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\')
$OutFile = [IO.Path]::GetFullPath($OutFile)
if ($OutFile.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to write the private key inside the repository ($repo). Pick a folder outside it." }
if (Test-Path -LiteralPath $OutFile) { throw "$OutFile already exists - refusing to overwrite a signing key." }
New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null

$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider 3072
try {
    # Private key file, readable by this account only (no inherited permissions).
    $sec = New-Object Security.AccessControl.FileSecurity
    $sec.SetAccessRuleProtection($true, $false)
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $sec.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow')))
    $bytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    # created WITH those permissions, so it is never readable by anyone else, not even for a moment
    $fs = if ($PSVersionTable.PSEdition -eq 'Core') { [IO.FileSystemAclExtensions]::Create((New-Object IO.FileInfo $OutFile), [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl, [IO.FileShare]::None, 4096, [IO.FileOptions]::None, $sec) }
          else { [IO.File]::Create($OutFile, 4096, [IO.FileOptions]::None, $sec) }
    try { $fs.Write($bytes, 0, $bytes.Length) } finally { $fs.Dispose() }

    $pub = $rsa.ExportParameters($false)
    $line = 'UpdatePublicKey=rsa:' + [Convert]::ToBase64String($pub.Modulus) + ':' + [Convert]::ToBase64String($pub.Exponent)
} finally { $rsa.Dispose() }

Write-Host ""
Write-Host "Private key written to $OutFile (only your account can read it)."
Write-Host "Move it OFFLINE and back it up. Anyone with this file can sign launcher updates."
Write-Host ""
Write-Host "Put this line in config\launcher.cfg (it is public - commit it):"
Write-Host ""
Write-Host $line
Write-Host ""
