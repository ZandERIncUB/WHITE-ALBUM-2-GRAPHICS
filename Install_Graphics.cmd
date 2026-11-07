@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_graphics.ps1" %*
if errorlevel 1 (
  echo.
  echo Graphics installation failed. See the message above.
  pause
  exit /b 1
)
echo.
echo Graphics installation completed.
pause
