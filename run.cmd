@echo off
REM Double-click or: run.cmd
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
