@echo off
setlocal
rem Pure ASCII on purpose: cmd re-reads this file by byte offset, and multibyte
rem UTF-8 art plus a mid-script chcp desyncs the parser into executing garbage.
rem The ESC char (forfiles' 0xNN trick) drives the ANSI colors instead.
for /F "delims=" %%E in ('forfiles /p "%~dp0." /m "%~nx0" /c "cmd /c echo(0x1B"') do set "ESC=%%E"

rem Repo root = the directory this script lives in (strip trailing backslash).
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"

rem Prefer the user-local SDK (exists inside the Claude Code sandbox, where the
rem PATH dotnet is runtime-only); fall back to dotnet on PATH, which on the real
rem desktop session carries the full SDK at C:\Program Files\dotnet.
set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
    where dotnet >nul 2>&1
    if errorlevel 1 (
        echo Could not find dotnet - neither %LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe nor dotnet on PATH.
        pause
        exit /b 1
    )
    set "DOTNET=dotnet"
)

rem A running Versio.exe locks its own binaries, so close it before publishing.
taskkill /IM Versio.exe /F >nul 2>&1
if not errorlevel 1 echo Closed running Versio.exe.

rem A host lingering from a crashed run holds artifacts\publish open, and the wipe
rem then fails with an unfriendly PowerShell error instead of this one-line insurance.
taskkill /IM Versio.Plc.TiaPortal.Host.exe /F >nul 2>&1
taskkill /IM Versio.Plc.TwinCat.Host.exe /F >nul 2>&1

rem The whole show lives in the script: banner, version menu, seven timed phases,
rem systems check, liftoff. Child output goes to artifacts\pack.log, not the console.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO%\tools\pack\Pack-Versio.ps1" -Repo "%REPO%" -Dotnet "%DOTNET%"

rem Exit 2 is the menu's Quit: nothing was touched, so neither banner applies.
if errorlevel 2 exit /b 0
if errorlevel 1 goto :abort
ping -n 3 127.0.0.1 >nul
exit /b 0

rem Pack failed: the script has already printed the reason and the tail of pack.log
rem right above this art, and the window stays open so it can actually be read.
:abort
echo(
echo %ESC%[91m   ####   #####    ####   #####   ######%ESC%[0m
echo %ESC%[91m  ##  ##  ##  ##  ##  ##  ##  ##    ##  %ESC%[0m
echo %ESC%[31m  ######  #####   ##  ##  #####     ##  %ESC%[0m
echo %ESC%[31m  ##  ##  ##  ##  ##  ##  ##  ##    ##  %ESC%[0m
echo %ESC%[31m  ##  ##  #####    ####   ##  ##    ##  %ESC%[0m
echo %ESC%[90m  ======================================%ESC%[0m
echo %ESC%[91m   PACKAGE FAILED - launch scrubbed.%ESC%[0m
echo %ESC%[37m   The output above says why.%ESC%[0m
echo(
pause
exit /b 1
