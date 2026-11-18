@echo off
rem ============================================================
rem  taiki: make figures from evacuation logs (double-click to run)
rem   - logs  : %USERPROFILE%\AppData\LocalLow\DefaultCompany\VRsimulator\EvacLogs
rem             and ...\Logs (taiki_PositionLogger)
rem   - layout: layout_testScene.json (same folder as this file)
rem   - output: figures\ (same folder as this file)
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"

set LOGROOT=%USERPROFILE%\AppData\LocalLow\DefaultCompany\VRsimulator
set LAYOUT=layout_testScene.json

set PY=python
where python >nul 2>nul
if errorlevel 1 (
  where py >nul 2>nul
  if errorlevel 1 (
    echo [ERROR] Python is not installed. Install it from https://www.python.org/ and check "Add python.exe to PATH".
    pause
    exit /b 1
  )
  set PY=py
)

echo Checking Python packages...
%PY% -m pip install --quiet pandas matplotlib numpy scipy

echo Making figures...
%PY% taiki_visualize_position.py "%LOGROOT%\EvacLogs" "%LOGROOT%\Logs" --layout "%LAYOUT%" --out figures
if errorlevel 1 (
  echo [ERROR] Failed. See the message above.
  pause
  exit /b 1
)

start "" "%~dp0figures"
echo Done. The figures folder has been opened.
pause
