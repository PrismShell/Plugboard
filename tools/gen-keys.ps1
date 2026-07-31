<#
.SYNOPSIS
  Generate the Plugboard signing key pair (the local root of trust). Run ONCE.
  Thin wrapper over the Plugboard.Sign .NET tool, because Windows PowerShell 5.1
  runs on .NET Framework and lacks the modern RSA export APIs.
  Keep the private key secret. Put the printed public key in the host's TrustedKeys.
#>
param([string]$OutDir = ".")

$tool = Join-Path $PSScriptRoot '..\src\Plugboard.Sign\bin\Release\net8.0\plugboard-sign.dll'
if (-not (Test-Path $tool)) { throw "Signer not built. Run: dotnet build $(Join-Path $PSScriptRoot '..\src\Plugboard.Sign') -c Release" }
dotnet $tool gen-keys $OutDir
