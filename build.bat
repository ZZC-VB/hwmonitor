@echo off
setlocal
cd /d "%~dp0"
if not exist bin\Release mkdir bin\Release
if not exist libs\LibreHardwareMonitorLib.dll (
  echo [错误] 缺少 libs\LibreHardwareMonitorLib.dll —— 先把 LHM 0.9.6 的 net472 DLL 及依赖放进 libs\
  echo        详见 README.md 的「依赖」一节
  exit /b 1
)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\vbc.exe" @build.rsp
if errorlevel 1 exit /b 1
copy /y libs\*.dll bin\Release\ >nul
if exist tools\PawnIO_setup.exe copy /y tools\PawnIO_setup.exe bin\Release\ >nul
echo 完成：bin\Release\HwMonitor.exe
