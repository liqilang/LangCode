@echo off
rem Build LangCode and produce dist\LangCode.vsix
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1" %*
if errorlevel 1 pause
