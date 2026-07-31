@echo off
REM Relative launcher for Plugboard - works wherever this repo folder lives.
REM Prefers the self-contained dist build; falls back to the Release build.
setlocal
set "EXE=%~dp0dist\Plugboard\Plugboard.Host.exe"
if not exist "%EXE%" set "EXE=%~dp0src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe"
if not exist "%EXE%" (
  echo Plugboard is not built yet.
  echo Build it with:  dotnet build src\Plugboard.Host\Plugboard.Host.csproj -c Release
  echo             and: powershell -ExecutionPolicy Bypass -File tools\build-plugins.ps1
  pause
  exit /b 1
)
start "" "%EXE%"
