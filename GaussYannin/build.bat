@echo off
rem ============================================================
rem  Gauss Yannin 一键构建打包（双击即可）
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause