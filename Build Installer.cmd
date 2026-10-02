@echo off
REM Clearspace | Builds the Clearspace installer.
setlocal EnableExtensions
cd /d "%~dp0"

set "APP=%CD%\installer-build\app"
set "RELEASE=%CD%\release"

REM NEW: the version the installer carries (and shows when it updates an older Clearspace).
set "VERSION="
set /p VERSION=<VERSION
if not defined VERSION goto :noversion

REM CHANGED (installer overhaul): looks for Inno Setup 7 as well as 6, newest first. The installer's dark
REM Clearspace look needs Inno Setup 6.6 or newer (6.7 or newer for the exact background color); an older
REM one still builds, with a plain light installer and a warning from the compiler.
set "ISCC="
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC goto :noinno

REM NEW: start from an empty payload folder, so the installer never ships files left over from an earlier build.
if exist "%APP%" rmdir /s /q "%APP%"
mkdir "%APP%"
if not exist "%RELEASE%" mkdir "%RELEASE%"

echo Building the Clearspace %VERSION% installer with "%ISCC%"
echo.
echo Publishing self-contained Clearspace...
REM FIXED: the self-contained build uses its own intermediate and output folders. Sharing obj\Release with
REM "Build Clearspace.cmd" let a later framework-dependent build reuse this build's precompiled (ReadyToRun)
REM code, which was compiled together with the bundled .NET - the result failed fast on startup.
REM NEW (dependencies): SelfContained=true puts .NET, WPF and SQLite inside Clearspace.exe, so the installer
REM needs nothing downloaded or installed beforehand. installer\Clearspace.iss refuses to build otherwise.
dotnet publish ".\Clearspace\Clearspace.csproj" -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:EnableCompressionInSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -p:IntermediateOutputPath=obj\Installer\ -p:OutputPath=bin\Installer\ -o "%APP%"
if errorlevel 1 goto :failed

REM NEW (journal catch-up): the optional index helper, self-contained so it runs without a separate .NET install.
echo Publishing the index helper...
dotnet publish ".\Clearspace.IndexHelper\Clearspace.IndexHelper.csproj" -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IntermediateOutputPath=obj\Installer\ -p:OutputPath=bin\Installer\ -o "%APP%"
if errorlevel 1 goto :failed

echo Building the installer...
echo Compressing the offline app can take around a minute with no progress line. Please wait for the Done message.
"%ISCC%" ".\installer\Clearspace.iss"
if errorlevel 1 goto :failed

echo.
echo Done.
echo Clearspace %VERSION% installer: "%RELEASE%\ClearspaceSetup.exe"
echo On a PC that already has Clearspace, the same file updates, repairs or removes it.
echo For the next release, raise the number in the VERSION file before building.
pause
exit /b 0

:noversion
echo The VERSION file is missing or empty. It holds the version number, for example 1.2.0.
echo.
pause
exit /b 1

:noinno
echo Inno Setup is required to build the Clearspace installer.
echo Install it once, then run this file again:
echo   winget install -e --id JRSoftware.InnoSetup
echo   or download it from https://jrsoftware.org/isdl.php
echo.
pause
exit /b 1

:failed
echo.
echo Installer build failed.
pause
exit /b 1
