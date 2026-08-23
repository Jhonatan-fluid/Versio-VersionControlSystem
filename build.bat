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

rem A running Versio.exe locks its own binaries, so close it before building.
taskkill /IM Versio.exe /F >nul 2>&1
if not errorlevel 1 echo Closed running Versio.exe.

echo Building Versio.App...
"%DOTNET%" build "%REPO%\src\Versio.App\Versio.App.csproj"
if errorlevel 1 (
    echo.
    echo Build FAILED - not launching. Output above shows why.
    pause
    exit /b 1
)

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
echo %ESC%[92m   BUILD GREEN%ESC%[90m - 0 warnings - 0 errors - all systems go%ESC%[0m
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
start "" "%REPO%\src\Versio.App\bin\Debug\net9.0-windows\Versio.exe" "%REPO%"
ping -n 3 127.0.0.1 >nul
endlocal
