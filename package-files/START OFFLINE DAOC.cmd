@echo off
setlocal
cd /d "%~dp0"
rem Offline DAoC 0.35 "Claude Takeover III". Uses the .NET runtime bundled in tools\dotnet,
rem so nothing has to be installed except the Windows .NET Framework 3.5 feature.
if not exist "%~dp0runtime\OfflineDAoC.exe" (
  echo This folder is incomplete. Extract the whole download again, then retry.
  pause
  exit /b 1
)
set "DOTNET_ROOT=%~dp0tools\dotnet"
set "DOTNET_ROOT_X64=%~dp0tools\dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
start "" "%~dp0runtime\OfflineDAoC.exe"
