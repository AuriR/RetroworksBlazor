@echo off
rem RetroWorks ][ launcher for Windows - double-click to start.
cd /d "%~dp0"
title RetroWorks ][
set PY=python
where python >nul 2>nul || set PY=py
%PY% --version >nul 2>nul
if errorlevel 1 (
  echo RetroWorks needs Python 3. Install it from https://www.python.org/downloads/
  echo and tick "Add Python to PATH" during setup, then run this file again.
  pause
  exit /b 1
)
%PY% -c "import curses" >nul 2>nul
if errorlevel 1 (
  echo First run: installing windows-curses...
  %PY% -m pip install --user windows-curses
)
%PY% retroworks.py %*
if errorlevel 1 pause
