@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo  Yangzai Workshop 一键打包
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*

set EXITCODE=%ERRORLEVEL%
echo.
if %EXITCODE%==0 (
    echo [成功] 打包完成！产物在 output 目录下。
) else (
    echo [失败] 打包出错，请查看上方日志。
)

echo.
pause
