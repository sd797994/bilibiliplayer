@echo off
setlocal

pushd "%~dp0"
if errorlevel 1 (
    echo Failed to open the repository directory.
    pause
    exit /b 1
)

set "PUBLISH_SCRIPT=%~dp0scripts\Publish-Windows.ps1"
if not exist "%PUBLISH_SCRIPT%" (
    echo Publish script was not found: "%PUBLISH_SCRIPT%"
    popd
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PUBLISH_SCRIPT%" %*
set "PUBLISH_EXIT_CODE=%ERRORLEVEL%"

popd

if not "%PUBLISH_EXIT_CODE%"=="0" (
    echo.
    echo Publish failed with exit code %PUBLISH_EXIT_CODE%.
    pause
    exit /b %PUBLISH_EXIT_CODE%
)

echo.
echo Publish completed successfully.
pause
exit /b 0
