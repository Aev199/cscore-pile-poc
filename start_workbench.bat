@echo off
cd /d "%~dp0"
python workbench_app.py
if errorlevel 1 pause
