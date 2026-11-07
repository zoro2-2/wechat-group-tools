@echo off
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [x] csc.exe not found. Please install .NET Framework 4.x
  exit /b 1
)

if not exist dist mkdir dist

echo [1/3] build icon ...
"%CSC%" /nologo /codepage:65001 /target:exe /out:dist\MakeIcon.exe src\MakeIcon.cs /r:System.Drawing.dll
if errorlevel 1 goto fail
dist\MakeIcon.exe app.ico

echo [2/3] build GroupHelper.exe ...
"%CSC%" /nologo /target:winexe /platform:x64 /codepage:65001 /win32manifest:src\app.manifest /win32icon:app.ico /out:dist\群好友助手.exe src\GroupHelper.cs src\AssemblyInfo.cs /r:System.Windows.Forms.dll /r:System.Drawing.dll
if errorlevel 1 goto fail

echo [3/3] build GetWxid.exe ...
"%CSC%" /nologo /target:winexe /platform:x64 /codepage:65001 /win32manifest:src\app.manifest /win32icon:app.ico /out:dist\取微信号.exe src\GetWxid.cs /r:System.Windows.Forms.dll /r:System.Drawing.dll
if errorlevel 1 goto fail

copy /y src\ocr.ps1 dist\ >nul

echo.
echo [OK] build finished, output in dist\
dir /b dist
exit /b 0

:fail
echo [x] build failed
exit /b 1