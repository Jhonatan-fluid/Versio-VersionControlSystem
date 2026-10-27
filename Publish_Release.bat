@echo off
setlocal
rem Pure ASCII on purpose, like Build_Package.bat: cmd re-reads this file by byte offset,
rem and multibyte UTF-8 plus a mid-script chcp desyncs the parser into executing garbage.
rem CRLF on purpose too: an LF-only batch file mis-resolves its own labels.
for /F "delims=" %%E in ('forfiles /p "%~dp0." /m "%~nx0" /c "cmd /c echo(0x1B"') do set "ESC=%%E"

rem Repo root = the directory this script lives in (strip trailing backslash).
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"

rem The same SDK choice as Build_Package.bat, for the same reason.
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

rem git is needed before the pack starts: the token, the version tag, the release notes.
where git >nul 2>&1
if errorlevel 1 (
    echo Could not find git on PATH. Publishing reads your GitHub sign-in and tags the release with it.
    pause
    exit /b 1
)

rem A running Versio.exe locks its own binaries, and a lingering host holds artifacts\publish.
taskkill /IM Versio.exe /F >nul 2>&1
if not errorlevel 1 echo Closed running Versio.exe.
taskkill /IM Versio.Plc.TiaPortal.Host.exe /F >nul 2>&1
taskkill /IM Versio.Plc.TwinCat.Host.exe /F >nul 2>&1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%REPO%\tools\pack\Publish-Versio.ps1" -Repo "%REPO%" -Dotnet "%DOTNET%"
set "RC=%ERRORLEVEL%"

rem The window is held whatever happened. Unlike a pack, a publish ends with things to read:
rem the release URL, whether the push went through, or which step failed and why.
rem Exit 0 published or drafted, 1 failed, 2 quit before anything was uploaded.
if "%RC%"=="0" (
    echo(
    echo %ESC%[92m   Done.%ESC%[0m
)
echo(
pause
exit /b %RC%
