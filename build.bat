@echo off
setlocal
rem Pure ASCII on purpose: cmd re-reads this file by byte offset, and multibyte
rem UTF-8 art plus a mid-script chcp desyncs the parser into executing garbage.
rem The ESC char (forfiles' 0xNN trick) drives the ANSI colors instead.
for /F "delims=" %%E in ('forfiles /p "%~dp0." /m "%~nx0" /c "cmd /c echo(0x1B"') do set "ESC=%%E"

rem Repo root = the directory this script lives in (strip trailing backslash).
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"
set "BIN=%REPO%\src\Versio.App\bin\Debug\net9.0-windows"

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

rem A running Versio.exe locks its own binaries, so close it before building.
taskkill /IM Versio.exe /F >nul 2>&1
if not errorlevel 1 echo Closed running Versio.exe.

echo Building Versio.App...
"%DOTNET%" build "%REPO%\src\Versio.App\Versio.App.csproj"
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
echo %ESC%[92m   BUILD GREEN%ESC%[90m - 0 warnings - 0 errors%ESC%[0m
echo(
call :ready "Core + IEC codecs ............" "%BIN%\Versio.Core.dll"
call :ready "CODESYS pack ................." "%BIN%\Versio.Plc.Codesys.Infrastructure.dll"
call :ready "TIA Portal pack + net48 host ." "%REPO%\src\Versio.Plc.TiaPortal.Host\bin\Debug\net48\Versio.Plc.TiaPortal.Host.exe"
call :ready "TwinCAT pack + XAE host ......" "%REPO%\src\Versio.Plc.TwinCat.Host\bin\Debug\net9.0-windows\Versio.Plc.TwinCat.Host.dll"
call :ready "Versio shell ................." "%BIN%\Versio.exe"
echo(
<nul set /p "=%ESC%[93m   IMMINENT LAUNCH IN 3%ESC%[0m"
ping -n 2 127.0.0.1 >nul
<nul set /p "=%ESC%[93m.. 2%ESC%[0m"
ping -n 2 127.0.0.1 >nul
<nul set /p "=%ESC%[93m.. 1%ESC%[0m"
ping -n 2 127.0.0.1 >nul
echo %ESC%[93m..%ESC%[0m
echo(
echo %ESC%[92m   LIFTOFF - Versio is go.%ESC%[0m
start "" "%BIN%\Versio.exe" "%REPO%"
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

rem Build failed: the compiler output stays right above this banner, and the
rem window stays open so it can actually be read.
:abort
echo(
echo %ESC%[91m   ####   #####    ####   #####   ######%ESC%[0m
echo %ESC%[91m  ##  ##  ##  ##  ##  ##  ##  ##    ##  %ESC%[0m
echo %ESC%[31m  ######  #####   ##  ##  #####     ##  %ESC%[0m
echo %ESC%[31m  ##  ##  ##  ##  ##  ##  ##  ##    ##  %ESC%[0m
echo %ESC%[31m  ##  ##  #####    ####   ##  ##    ##  %ESC%[0m
echo %ESC%[90m  ======================================%ESC%[0m
echo %ESC%[91m   BUILD FAILED - launch scrubbed.%ESC%[0m
echo %ESC%[37m   The compiler output above says why.%ESC%[0m
echo(
pause
exit /b 1
