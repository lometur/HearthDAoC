@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Connect your OfflineDAoC client to a HearthDAoC server.
rem Put this file in your official OfflineDAoC install's runtime\client-opendaoc\app folder
rem (next to connect.exe). Settings are saved in hearthdaoc.cfg next to this file. When
rem patch-client.ps1 and the patches folder are there too, every start first runs patch-client.ps1 once
rem for each patch set in it: classic-creation.json (HearthDAoC's classic character creation and splash),
rem default-layout.json (the window layout new characters start with), si-portals.json (no Shrouded
rem Isles portal art in Cotswold, Mularn and Mag Mell) and si-portal-art.json (the 2002 look of the
rem Albion one). Without them, there is no patch step; without one set, there is none for it.
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
rem Exit 3 is a client file the set doesn't support (e.g. the b edition's game.dll): nothing of
rem that set changes, and the game starts without it (the standard creation screen). Any other
rem failure warns, then the game starts anyway. Each set runs on its own: one refused or failed
rem never stops the others. A retired set (a release that takes it back) is restored by the same run.
rem cmd /d /c runs it so that a batch file named powershell (the tests' stand-in) returns here too,
rem and keeps a ^ in the folder name as it is, which call would double.
if not exist "%~dp0patch-client.ps1" goto connect
if not exist "%~dp0patches\classic-creation.json" goto layout
if defined DRYRUN echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1"
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto layout
if "%RC%"=="3" goto layout
echo Warning: HearthDAoC's client patches could not be applied (patch-client.ps1 exit %RC%), so the classic creation screen may be missing. To fix it, run patch-client.bat; for an install under Program Files, run it once as administrator.
pause
:layout
if not exist "%~dp0patches\default-layout.json" goto siportals
if defined DRYRUN echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\default-layout.json"
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\default-layout.json"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto siportals
if "%RC%"=="3" goto siportals
echo Warning: HearthDAoC's default layout could not be applied (patch-client.ps1 exit %RC%), so new characters may start with the game's standard window layout. To fix it, run patch-client.bat; for an install under Program Files, run it once as administrator.
pause
:siportals
if not exist "%~dp0patches\si-portals.json" goto portalart
if defined DRYRUN echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portals.json"
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portals.json"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto portalart
if "%RC%"=="3" goto portalart
echo Warning: HearthDAoC's Shrouded Isles portal fix could not be applied (patch-client.ps1 exit %RC%), so upstream's portal art may still stand in Cotswold, Mularn and Mag Mell. To fix it, run patch-client.bat; for an install under Program Files, run it once as administrator.
pause
:portalart
if not exist "%~dp0patches\si-portal-art.json" goto connect
if defined DRYRUN echo powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portal-art.json"
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portal-art.json"
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" goto connect
if "%RC%"=="3" goto connect
echo Warning: HearthDAoC's Albion portal look could not be applied (patch-client.ps1 exit %RC%), so the Albion Shrouded Isles portal may not have this release's look. To fix it, run patch-client.bat; for an install under Program Files, run it once as administrator.
pause
:connect
if defined DRYRUN (
    echo connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
    exit /b 0
)
start "" connect.exe game.dll "%SERVER%" "%ACCOUNT%" "%PASSWORD%"
