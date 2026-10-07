@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Connect your OfflineDAoC client to a HearthDAoC server.
rem Put this file in your official OfflineDAoC install's runtime\client-opendaoc\app folder
rem (next to connect.exe). Settings are saved in hearthdaoc.cfg next to this file. When
rem patch-client.ps1 and the patches folder are there too, every start first runs patch-client.ps1
rem (HearthDAoC's classic character creation and splash); without them, there is no patch step.
rem Every value is used inside double quotes, where cmd takes & | < > ^ literally, and delayed
rem expansion stays off so ! passes through too. Values must not contain double quotes or %.
cd /d "%~dp0"
if not exist connect.exe (
    echo connect.exe was not found. Put this file in runtime\client-opendaoc\app of your OfflineDAoC install.
    pause
    exit /b 1
)
set "CFG=%~dp0hearthdaoc.cfg"
if exist "%CFG%" goto load
rem Each prompt is skipped when the value is already set in the environment (scripted installs).
if not defined SERVER set /p "SERVER=Server address (host:port, ask the server owner): "
if not defined ACCOUNT set /p "ACCOUNT=Account name (letters and digits): "
if not defined PASSWORD set /p "PASSWORD=Password (use one you use nowhere else; no spaces, double quotes or %%): "
> "%CFG%" echo "SERVER=%SERVER%"
>> "%CFG%" echo "ACCOUNT=%ACCOUNT%"
>> "%CFG%" echo "PASSWORD=%PASSWORD%"
:load
for /f "usebackq delims=" %%L in ("%CFG%") do set %%L
rem The patch step: it patches files that something put back, and only reads a patched client.
rem Exit 3 is a client the patches don't support (e.g. the b edition): the standard creation
rem screen. Any other failure warns, then the game starts anyway. cmd /d /c runs it so that a
rem batch file named powershell (the tests' stand-in) returns here too, and keeps a ^ in the
rem folder name as it is, which call would double.
if not exist "%~dp0patch-client.ps1" goto connect
if not exist "%~dp0patches\classic-creation.json" goto connect
if defined DRYRUN echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1"
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto connect
if "%RC%"=="3" goto connect
echo Warning: HearthDAoC's client patches could not be applied (patch-client.ps1 exit %RC%), so the classic creation screen may be missing. To fix it, run patch-client.bat; for an install under Program Files, run it once as administrator.
pause
:connect
if defined DRYRUN (
    echo connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
    exit /b 0
)
start "" connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
