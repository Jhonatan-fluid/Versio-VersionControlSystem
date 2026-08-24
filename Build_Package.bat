@echo off
setlocal
rem Pure ASCII on purpose: cmd re-reads this file by byte offset, and multibyte
rem UTF-8 art plus a mid-script chcp desyncs the parser into executing garbage.
rem The ESC char (forfiles' 0xNN trick) drives the ANSI colors instead.
for /F "delims=" %%E in ('forfiles /p "%~dp0." /m "%~nx0" /c "cmd /c echo(0x1B"') do set "ESC=%%E"

rem Repo root = the directory this script lives in (strip trailing backslash).
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"
set "PUBLISH=%REPO%\artifacts\publish"
set "RELEASES=%REPO%\artifacts\releases"

rem The version being packed, from the single source of truth, so the banner can say
rem WHICH Versio just landed - the only other place it appears is inside vpk's output.
set "VERLINE="
for /f "delims=" %%V in ('findstr /c:"<VersioVersion>" "%REPO%\Directory.Build.props"') do set "VERLINE=%%V"
if not defined VERLINE set "VERLINE=<VersioVersion>unknown</VersioVersion>"
set "VER=%VERLINE:*<VersioVersion>=%"
set "VER=%VER:</VersioVersion>=%"

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

echo Fetching pack deps, publishing, packing Setup.exe and MSI...
echo First run downloads PortableGit and vpk. Later runs reuse the cache.
echo(
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO%\tools\pack\Pack-Versio.ps1" -Repo "%REPO%" -Dotnet "%DOTNET%"
if errorlevel 1 goto :abort

cls
echo(
echo %ESC%[97m  ##   ##  #######  ######    #####   ####   #######  %ESC%[0m
echo %ESC%[96m  ##   ##  ##       ##   ##  ##        ##   ##     ## %ESC%[0m
echo %ESC%[96m  ##   ##  #####    ######    #####    ##   ##     ## %ESC%[0m
echo %ESC%[36m   ## ##   ##       ##   ##       ##   ##   ##     ## %ESC%[0m
echo %ESC%[34m    ###    #######  ##   ##   #####   ####   #######  %ESC%[0m
echo %ESC%[90m  ==================================================  %ESC%[0m
echo %ESC%[37m           PLC projects, versioned in git             %ESC%[0m
echo(
echo %ESC%[92m   PACKAGE GREEN%ESC%[90m - Versio %VER% - Setup.exe and MSI are on disk%ESC%[0m
echo(
call :ready "Self-contained Versio.exe ...." "%PUBLISH%\Versio.exe"
call :ready "Bundled git.exe .............." "%PUBLISH%\git\cmd\git.exe"
call :ready "TIA Portal host .............." "%PUBLISH%\tia\Versio.Plc.TiaPortal.Host.exe"
call :ready "TwinCAT host ................." "%PUBLISH%\twincat\Versio.Plc.TwinCat.Host.exe"
call :any "Setup.exe ...................." "%RELEASES%\*Setup.exe"
call :any "Per-machine MSI .............." "%RELEASES%\*.msi"
echo(
echo %ESC%[92m   LIFTOFF - artifacts\releases is go.%ESC%[0m
echo %ESC%[90m   Not launching Setup: that would install this machine.%ESC%[0m
start "" "%RELEASES%"
ping -n 3 127.0.0.1 >nul
exit /b 0

rem One line of the systems check: green OK when the built artifact is on disk,
rem red MISSING when it is not - so the list reports the disk, not optimism.
:ready
if exist "%~2" (
    echo %ESC%[92m   [ OK ]%ESC%[0m %ESC%[37m%~1%ESC%[0m %ESC%[92mready%ESC%[0m
) else (
    echo %ESC%[91m   [FAIL]%ESC%[0m %ESC%[37m%~1%ESC%[0m %ESC%[91mMISSING%ESC%[0m
)
exit /b 0

rem Same as :ready, for a glob (Setup.exe / MSI names come from vpk).
:any
set "_FOUND="
for %%F in ("%~2") do if exist "%%~fF" set "_FOUND=1"
if defined _FOUND (
    echo %ESC%[92m   [ OK ]%ESC%[0m %ESC%[37m%~1%ESC%[0m %ESC%[92mready%ESC%[0m
) else (
    echo %ESC%[91m   [FAIL]%ESC%[0m %ESC%[37m%~1%ESC%[0m %ESC%[91mMISSING%ESC%[0m
)
exit /b 0

rem Pack failed: the compiler / vpk output stays right above this banner, and the
rem window stays open so it can actually be read.
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
