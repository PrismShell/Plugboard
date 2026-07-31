<#
.SYNOPSIS
  Register (or remove) the .pbapp file type as a per-user (HKCU - no admin) association
  whose DEFAULT program is Plugboard. Double-clicking a .pbapp opens it THROUGH
  Plugboard (served at localhost -> same-origin), so its capability calls work with no
  key. A drive-by web page can't trigger a double-click, so this path isn't attacker-reachable.

  (The older right-click "Serve via Plugboard" verb on .html has been removed; this
  script strips it on run so only the .pbapp association remains.)

.EXAMPLE
  .\register-open-with-gateway.ps1
  .\register-open-with-gateway.ps1 -Remove
#>
param(
  [string]$ExePath = "$PSScriptRoot\..\src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe",
  [switch]$Remove
)

# Always drop the retired right-click verbs on .html so only the .pbapp association is left.
Remove-Item 'HKCU:\Software\Classes\SystemFileAssociations\.html\shell\ServeViaPlugboard' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Classes\SystemFileAssociations\.html\shell\ServeViaGateway'     -Recurse -Force -ErrorAction SilentlyContinue

$progId  = 'Plugboard.App'
$extKey  = 'HKCU:\Software\Classes\.pbapp'
$progKey = "HKCU:\Software\Classes\$progId"

if ($Remove) {
  Remove-Item $extKey  -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $progKey -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host "Removed the .pbapp association."
  return
}

$resolved = Resolve-Path $ExePath -ErrorAction SilentlyContinue
$exe = if ($resolved) { $resolved.Path } else { $null }
if (-not $exe) { throw "Plugboard exe not found at $ExePath - build it first, or pass -ExePath." }

$cmd = '"{0}" --open "%1"' -f $exe
New-Item -Path $extKey -Force | Out-Null
Set-ItemProperty -Path $extKey -Name '(default)' -Value $progId
New-Item -Path $progKey -Force | Out-Null
Set-ItemProperty -Path $progKey -Name '(default)' -Value 'Plugboard App'
New-Item -Path "$progKey\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$progKey\DefaultIcon" -Name '(default)' -Value $exe
New-Item -Path "$progKey\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$progKey\shell\open\command" -Name '(default)' -Value $cmd

Write-Host "Registered .pbapp default program -> `"$exe`" --open `"%1`" (double-click opens in Plugboard)."
Write-Host "Plugboard must be running when you open a .pbapp."
