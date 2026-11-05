@echo off
rem Output text is ASCII on purpose: cmd.exe decodes .bat with the system code
rem page, so non-ASCII text breaks on English CI runners. Chinese walkthrough
rem lives in README.md.
setlocal
cd /d "%~dp0"

if not exist bin\Release mkdir bin\Release

if not exist libs\LibreHardwareMonitorLib.dll (
  echo [ERROR] libs\LibreHardwareMonitorLib.dll not found.
  echo         Download LibreHardwareMonitor 0.9.6 "LibreHardwareMonitor.zip"
  echo         and copy its DLLs into libs\ ; see libs\README.md
  exit /b 1
)

set VBC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\vbc.exe
if not exist "%VBC%" (
  echo [ERROR] vbc.exe not found. .NET Framework 4.8 is required.
  exit /b 1
)

"%VBC%" @build.rsp
if errorlevel 1 exit /b 1

copy /y libs\*.dll bin\Release\ >nul
if exist tools\PawnIO_setup.exe copy /y tools\PawnIO_setup.exe bin\Release\ >nul

echo Done: bin\Release\HwMonitor.exe
