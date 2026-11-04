#requires -Version 7
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$KeyPath, [Parameter(Mandatory=$true)][string]$ManifestPath,
    [Parameter(Mandatory=$true)][string]$SignaturePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$key = [IO.Path]::GetFullPath($KeyPath)
if ($key.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '发布私钥必须保存在仓库外。' }
if ((Get-Item -LiteralPath $key -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '私钥文件不能是链接。' }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$encrypted = [IO.File]::ReadAllBytes($key)
$plain = $null; $signer = [Security.Cryptography.RSA]::Create(); $verifier = [Security.Cryptography.RSA]::Create()
try {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $consumed = 0
    $signer.ImportPkcs8PrivateKey($plain,[ref]$consumed)
    if ($consumed -ne $plain.Length -or $signer.KeySize -lt 3072) { throw '发布私钥格式或长度无效。' }
    $verifier.ImportFromPem([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'update-public.pem')))
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($signer.ExportSubjectPublicKeyInfo(),$verifier.ExportSubjectPublicKeyInfo())) { throw '私钥与固定发布公钥不匹配。' }
    $manifest = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($ManifestPath))
    if ($manifest.Length -eq 0 -or $manifest.Length -gt 16384) { throw '更新清单大小无效。' }
    $signature = $signer.SignData($manifest,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    if (-not $verifier.VerifyData($manifest,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw '签名复核失败。' }
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($SignaturePath),$signature)
} finally {
    if ($null -ne $plain) { [Security.Cryptography.CryptographicOperations]::ZeroMemory($plain) }
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($encrypted)
    $signer.Dispose(); $verifier.Dispose()
}
