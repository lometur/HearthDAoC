@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Connect your OfflineDAoC client to a HearthDAoC server.
rem Put this file in your official OfflineDAoC install's runtime\client-opendaoc\app folder
rem (next to connect.exe). Settings are saved in hearthdaoc.cfg next to this file.
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
if defined DRYRUN (
    echo connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
    exit /b 0
)
start "" connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
