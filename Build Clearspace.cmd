@echo off
REM Clearspace | Builds a local Clearspace release and desktop shortcut.
setlocal
cd /d "%~dp0Clearspace"

echo Publishing Clearspace...
echo.

taskkill /IM Clearspace.exe /F >nul 2>&1
if not errorlevel 1 timeout /t 1 /nobreak >nul

REM FIXED: never reuse precompiled code left by an older self-contained installer build (it made
REM dist\Clearspace.exe fail fast on startup). Cheap to redo: only Clearspace itself is precompiled here.
if exist "obj\Release\net10.0-windows\win-x64\R2R" rmdir /s /q "obj\Release\net10.0-windows\win-x64\R2R"

dotnet publish -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo Publish failed. See the errors above.
    pause
    exit /b 1
)

REM NEW (journal catch-up): the optional index helper service lives next to Clearspace.exe.
echo.
echo Publishing the index helper...
dotnet publish "%~dp0Clearspace.IndexHelper\Clearspace.IndexHelper.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo Publishing the index helper failed. See the errors above.
    pause
    exit /b 1
)

echo.
echo Creating desktop shortcut...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Clearspace.lnk');" ^
  "$s.TargetPath = '%~dp0dist\Clearspace.exe';" ^
  "$s.WorkingDirectory = '%~dp0dist';" ^
  "$s.Description = 'Clearspace file manager';" ^
  "$s.Save()"

echo.
echo Done.
echo   Executable:  %~dp0dist\Clearspace.exe
echo   Shortcut:    Desktop\Clearspace
echo.
echo Rerun this script only after changing the code.
pause
endlocal
