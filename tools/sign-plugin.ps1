<#
.SYNOPSIS
  Sign a plugin DLL with the Plugboard private key, producing <dll>.sig.
  Thin wrapper over the Plugboard.Sign .NET tool (shares the host's crypto).
.EXAMPLE
  .\sign-plugin.ps1 -Dll path\to\MyPlugin.dll -Key plugboard-private.key
#>
param(
  [Parameter(Mandatory)][string]$Dll,
  [string]$Key = 'plugboard-private.key'
)

$tool = Join-Path $PSScriptRoot '..\src\Plugboard.Sign\bin\Release\net8.0\plugboard-sign.dll'
if (-not (Test-Path $tool)) { throw "Signer not built. Run: dotnet build $(Join-Path $PSScriptRoot '..\src\Plugboard.Sign') -c Release" }
dotnet $tool sign $Dll $Key
