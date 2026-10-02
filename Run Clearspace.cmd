@echo off
REM Clearspace | Builds and launches Clearspace for development.
setlocal
cd /d "%~dp0Clearspace"

taskkill /IM Clearspace.exe /F >nul 2>&1
if not errorlevel 1 timeout /t 1 /nobreak >nul

dotnet build -c Debug -v:quiet --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
start "" "bin\Debug\net10.0-windows\win-x64\Clearspace.exe"
endlocal
