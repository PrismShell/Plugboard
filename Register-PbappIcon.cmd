@echo off
REM Register the .pbapp file icon (the transparent plug) for the current user.
REM No admin needed - writes under HKCU only. Points the existing Plugboard.App ProgID's
REM DefaultIcon at the plug icon shipped next to Plugboard.Host.exe; the .pbapp open
REM command (Plugboard.Host --open) is left untouched.
setlocal
set "ICO=%~dp0src\Plugboard.Host\bin\Release\net8.0-windows\pbapp.ico"
if not exist "%ICO%" ( echo Icon not found: "%ICO%"  ^(build Plugboard.Host in Release first^) & pause & exit /b 1 )

reg add "HKCU\Software\Classes\.pbapp" /ve /d "Plugboard.App" /f >nul
reg add "HKCU\Software\Classes\Plugboard.App" /ve /d "Plugboard App" /f >nul
reg add "HKCU\Software\Classes\Plugboard.App\DefaultIcon" /ve /d "%ICO%" /f >nul
if errorlevel 1 ( echo FAILED to write registry. & pause & exit /b 1 )

ie4uinit.exe -show >nul 2>&1

echo Done. .pbapp files now use the plug icon:
echo   %ICO%
echo (If Explorer still shows the old icon, sign out/in or restart explorer.exe.)
pause
