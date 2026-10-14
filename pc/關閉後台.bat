@echo off
chcp 65001 >nul
start "" "%~dp0AndroidCast.exe" --quit
exit /b 0
