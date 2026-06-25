@echo off
setlocal

:: ─── Banyan Lite Windows MSI builder ────────────────────────────────────────
:: Prerequisites:
::   dotnet tool install --global wix
::   wix extension add WixToolset.UI.wixext
::   wix extension add WixToolset.Util.wixext
::
:: Run from this directory on a Windows machine or Windows CI runner.
:: The publish output (banyan.exe + companions) must already exist at
::   ..\..\publish\win-x64\
:: before calling this script.
::
:: Usage:
::   build-installer.cmd [VERSION]
::   build-installer.cmd 1.2.3
::
:: If VERSION is omitted, it is read from banyan.exe's file version.
:: ─────────────────────────────────────────────────────────────────────────────

set SCRIPT_DIR=%~dp0
set PUBLISH_DIR=%SCRIPT_DIR%..\..\publish\win-x64
set EXE=%PUBLISH_DIR%\banyan.exe

if not exist "%EXE%" (
    echo ERROR: %EXE% not found.
    echo Run the publish step first:
    echo   dotnet publish src\Banyan.Cli\Banyan.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\win-x64
    exit /b 1
)

:: Resolve version
if "%~1"=="" (
    for /f "tokens=*" %%v in ('powershell -NoProfile -Command "(Get-Item '%EXE%').VersionInfo.FileVersion"') do set VERSION=%%v
) else (
    set VERSION=%~1
)

if "%VERSION%"=="" (
    echo ERROR: Could not determine version from %EXE%. Pass it explicitly: build-installer.cmd 1.1.0
    exit /b 1
)

set OUT=%SCRIPT_DIR%banyan-lite-%VERSION%.msi

echo Building Banyan Lite %VERSION% installer...
echo   source : %SCRIPT_DIR%banyan.wxs
echo   bindir : %PUBLISH_DIR%
echo   output : %OUT%
echo.

wix build "%SCRIPT_DIR%banyan.wxs" ^
    -b "%PUBLISH_DIR%" ^
    -ext WixToolset.UI.wixext ^
    -o "%OUT%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo ERROR: wix build failed (exit %ERRORLEVEL%).
    exit /b %ERRORLEVEL%
)

echo.
echo Done: %OUT%
endlocal
