@echo off
setlocal EnableExtensions
rem Connect this OfflineDAoC client to the central server.
rem Put this file in your official OfflineDAoC install's runtime\client-opendaoc\app folder
rem (next to connect.exe). Settings are saved in central-server.cfg next to this file.
cd /d "%~dp0"
if not exist connect.exe (
    echo connect.exe was not found. Put this file in runtime\client-opendaoc\app of your OfflineDAoC install.
    pause
    exit /b 1
)
set "CFG=%~dp0central-server.cfg"
if exist "%CFG%" goto load
set /p "SERVER=Server address (host:port, ask the server owner): "
set /p "ACCOUNT=Account name (letters and digits): "
set /p "PASSWORD=Password (use one you use nowhere else, no spaces): "
> "%CFG%" echo SERVER=%SERVER%
>> "%CFG%" echo ACCOUNT=%ACCOUNT%
>> "%CFG%" echo PASSWORD=%PASSWORD%
:load
for /f "usebackq tokens=1,* delims==" %%A in ("%CFG%") do set "%%A=%%B"
if defined DRYRUN (
    echo connect.exe game.dll %SERVER% %ACCOUNT% %PASSWORD%
    exit /b 0
)
start "" connect.exe game.dll %SERVER% %ACCOUNT% %PASSWORD%
