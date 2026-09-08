@echo off
REM Clearspace | Builds the Clearspace installer.
setlocal EnableExtensions
cd /d "%~dp0"

set "APP=%CD%\installer-build\app"
set "RELEASE=%CD%\release"
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not exist "%APP%" mkdir "%APP%"
if not exist "%RELEASE%" mkdir "%RELEASE%"

if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 is required to build the Clearspace installer.
  echo Install it once from https://jrsoftware.org/isdl.php, then run this file again.
  echo.
  pause
  exit /b 1
)

echo Publishing self-contained Clearspace...
dotnet publish ".\Clearspace\Clearspace.csproj" -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o "%APP%"
if errorlevel 1 goto :failed

echo Building visible installer and updater...
echo Compressing the offline app can take around a minute with no progress line. Please wait for the Done message.
"%ISCC%" ".\installer\Clearspace.iss"
if errorlevel 1 goto :failed

echo.
echo Done.
echo One-click installer: "%RELEASE%\ClearspaceSetup.exe"
echo Run a newer setup later to update Clearspace in place.
pause
exit /b 0

:failed
echo.
echo Installer build failed.
pause
exit /b 1
