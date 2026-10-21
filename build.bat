@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "DEST=D:\Software\MiniThemeSwitch"

if not exist "%DEST%" mkdir "%DEST%"

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:"%~dp0app.ico" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:"%DEST%\MiniThemeSwitch.exe" "%~dp0Program.cs"

if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

echo Built: %DEST%\MiniThemeSwitch.exe
start "" "%DEST%\MiniThemeSwitch.exe"
