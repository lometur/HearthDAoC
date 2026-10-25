@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Give your OfflineDAoC client HearthDAoC's patches: the classic character creation screen, the
rem window layout new characters start with, and the Shrouded Isles portals (no portal art in
rem Cotswold, Mularn and Mag Mell; the 2002 look of the Albion one).
rem Put this file, patch-client.ps1 and the patches folder next to connect-hearthdaoc.bat
rem (runtime\client-opendaoc\app of your OfflineDAoC install) and double-click it. Run it again
rem whenever something puts the original files back. Options go to patch-client.ps1:
rem -Check only reports, -Restore puts the original files back. It runs patch-client.ps1 once for
rem each patch set in the patches folder (classic-creation.json, default-layout.json, si-portals.json,
rem then si-portal-art.json), each on its own, or once for the one -PatchSet names. The exit code is
rem the first failed run's, else 0.
rem cmd /d /c runs it so that a batch file named powershell (the tests' stand-in) returns here too.
set "ONE="
call :scan %*
if not exist "%~dp0patches\classic-creation.json" if not exist "%~dp0patches\default-layout.json" if not exist "%~dp0patches\si-portals.json" if not exist "%~dp0patches\si-portal-art.json" set "ONE=1"
if defined ONE goto one
set "RC=0"
if not exist "%~dp0patches\classic-creation.json" goto layout
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\classic-creation.json" %*
set "RC=%ERRORLEVEL%"
:layout
if not exist "%~dp0patches\default-layout.json" goto siportals
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\default-layout.json" %*
if "%RC%"=="0" set "RC=%ERRORLEVEL%"
:siportals
if not exist "%~dp0patches\si-portals.json" goto portalart
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portals.json" %*
if "%RC%"=="0" set "RC=%ERRORLEVEL%"
:portalart
if not exist "%~dp0patches\si-portal-art.json" goto done
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" -PatchSet "%~dp0patches\si-portal-art.json" %*
if "%RC%"=="0" set "RC=%ERRORLEVEL%"
goto done
:one
cmd /d /c powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*
set "RC=%ERRORLEVEL%"
:done
rem Keep the window open when started from Explorer, whose command line names this file.
rem The quotes are removed first, so the comparison is safe for any folder name.
setlocal EnableDelayedExpansion
set "LINE=!CMDCMDLINE!"
if defined LINE set "LINE=!LINE:"=!"
if defined LINE if /i not "!LINE:%~nx0=!"=="!LINE!" pause
exit /b %RC%

:scan
rem Sets ONE when an argument names a patch set or asks for help: then patch-client.ps1 runs once.
if "%~1"=="" exit /b 0
if /i "%~1"=="-PatchSet" set "ONE=1"
if /i "%~1"=="--PatchSet" set "ONE=1"
if /i "%~1"=="-Help" set "ONE=1"
if /i "%~1"=="--Help" set "ONE=1"
shift
goto scan
