<#
.SYNOPSIS
  Build every Plugboard plugin and deploy it into the host's plugins directory
  so the host loads it on next launch. Ends the hand-copying step.

  Each plugin deploys into its own subfolder (plugins/<Name>/) carrying its FULL
  dependency closure (deps.json + every dependency DLL + native bits), so the
  plugin's load context is dependency-complete. Plugboard.Contracts.dll is never
  copied: the host shares its own copy, and a duplicate breaks IPlugin type identity.

  Lite mode by default (no signing). Pass -Key to sign each plugin for the managed
  posture (RequireSignature=true on the host).

.EXAMPLE
  .\build-plugins.ps1
  .\build-plugins.ps1 -Configuration Release -Key plugboard-private.key
  .\build-plugins.ps1 -PluginsDir 'C:\path\to\some\plugins'
#>
param(
  [string]$Configuration = 'Release',
  [string]$PluginsDir,                 # default: the host build output's plugins/
  [string]$Key                         # optional: sign each plugin (managed mode)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $PluginsDir) {
  $PluginsDir = Join-Path $root "src\Plugboard.Host\bin\$Configuration\net8.0-windows\plugins"
}
New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null

# Clean prior Plugboard plugin artifacts (flat DLLs + per-plugin subfolders) so a
# rebuild cannot leave a stale copy that shadows the fresh one. Only touches
# Plugboard.Plugins.* and never anything else that may live in the dir.
Get-ChildItem $PluginsDir -Filter 'Plugboard.Plugins.*' -ErrorAction SilentlyContinue |
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$projects = Get-ChildItem (Join-Path $root 'src\plugins') -Directory
$deployed = @()
foreach ($proj in $projects) {
  $csproj = Join-Path $proj.FullName "$($proj.Name).csproj"
  if (-not (Test-Path $csproj)) { continue }

  Write-Host "Building $($proj.Name)..." -ForegroundColor Cyan
  dotnet build $csproj -c $Configuration -v quiet
  if ($LASTEXITCODE -ne 0) { Write-Warning "  build failed, skipping $($proj.Name)"; continue }

  # The real runtime output is the build dir containing <Name>.deps.json. Keying on
  # deps.json skips the 'ref/' reference-assembly folder and is TFM-agnostic
  # (net8.0 vs net8.0-windows). The earlier bug was a -Recurse -Filter that could
  # match the metadata-only ref assembly, whose folder carries no dependencies.
  $outDir = Get-ChildItem (Join-Path $proj.FullName "bin\$Configuration") -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "$($proj.Name).deps.json") } |
            Select-Object -First 1 -ExpandProperty FullName
  if (-not $outDir) { Write-Warning "  no runtime output, skipping $($proj.Name)"; continue }

  # Deploy the WHOLE output folder (deps.json + every dependency + native bits).
  # Never copy Plugboard.Contracts (shared from the host). Skip pdbs to stay lean.
  $dest = Join-Path $PluginsDir $proj.Name
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  # Never copy assemblies the host SHARES from its default context: Contracts (IPlugin
  # type identity) and the BLPAPI stack (one managed wrapper + one session per process).
  # A private per-plugin copy of BLPAPI would break with "Session Not Started".
  Copy-Item (Join-Path $outDir '*') $dest -Recurse -Force -Exclude `
    'Plugboard.Contracts.dll','Plugboard.Blpapi.dll','Bloomberglp.Blpapi.dll','Bloomberglp.Blpapi.xml','*.pdb'

  # Optional manifest: <project>\plugin.json travels with the plugin if present
  # (id / version / display name / declared prerequisites for the catalog).
  $manifest = Join-Path $proj.FullName 'plugin.json'
  if (Test-Path $manifest) { Copy-Item $manifest $dest -Force }

  if ($Key) {
    & (Join-Path $PSScriptRoot 'sign-plugin.ps1') -Dll (Join-Path $dest "$($proj.Name).dll") -Key $Key
  }

  $deployed += $proj.Name
  Write-Host "  -> $dest" -ForegroundColor Green
}

Write-Host ""
Write-Host "Deployed $($deployed.Count) plugin(s) to:" -ForegroundColor Green
Write-Host "  $PluginsDir"
Write-Host "  $($deployed -join ', ')"
Write-Host "Restart the host to load them." -ForegroundColor Yellow
