@echo off
cd /d "%~dp0"

echo ==================================
echo  Yangzai Workshop MSI Build
echo ==================================
echo.

REM Step 1: Clean
echo [1/4] Cleaning old files...
if exist "..\publish" rmdir /s /q "..\publish"
if exist "output" rmdir /s /q "output"
mkdir output

REM Step 2: Publish (framework-dependent, Assets exposed)
echo [2/4] dotnet publish...
dotnet publish "..\YangzaiWorkshop.csproj" -c Release -r win-x64 --no-self-contained -o "..\publish"
if %ERRORLEVEL% neq 0 (
    echo [FAIL] Publish failed
    pause
    exit /b 1
)
echo        Done.

REM Step 3: Copy all Assets from source
echo [3/4] Copying Assets files...
xcopy "..\Assets\*" "..\publish\Assets\" /E /Y /I /Q

REM Step 4: Build MSI
echo [4/4] Building MSI...
wix build -acceptEula wix7 -ext WixToolset.UI.wixext Product.wxs -o "output\YangzaiManager.msi"
if %ERRORLEVEL% neq 0 (
    echo [FAIL] MSI build failed
    pause
    exit /b 1
)

echo.
echo ==================================
echo   SUCCESS!
echo   MSI: %~dp0output\YangzaiManager.msi
echo ==================================
pause
