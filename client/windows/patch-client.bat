@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Give your OfflineDAoC client HearthDAoC's classic character creation screen.
rem Put this file, patch-client.ps1 and the patches folder next to connect-hearthdaoc.bat
rem (runtime\client-opendaoc\app of your OfflineDAoC install) and double-click it. Run it again
rem whenever something puts the original files back. Options go to patch-client.ps1:
rem -Check only reports, -Restore puts the original files back.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0patch-client.ps1" %*
set "RC=%ERRORLEVEL%"
rem Keep the window open when started from Explorer, whose command line names this file.
rem The quotes are removed first, so the comparison is safe for any folder name.
setlocal EnableDelayedExpansion
set "LINE=!CMDCMDLINE!"
if defined LINE set "LINE=!LINE:"=!"
if defined LINE if /i not "!LINE:%~nx0=!"=="!LINE!" pause
exit /b %RC%
