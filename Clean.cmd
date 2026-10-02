@echo off
REM Clearspace | Removes generated build output.
setlocal
cd /d "%~dp0"

echo Removing build output from:
echo   %~dp0
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root = $PWD.Path;" ^
  "$targets = @();" ^
  "$targets += Get-ChildItem -Path $root -Directory -Filter 'publish*';" ^
  "$targets += Get-ChildItem -Path $root -Directory | Where-Object { $_.Name -eq 'release' };" ^
  "$targets += Get-ChildItem -Path $root -Recurse -Directory -Force | Where-Object { $_.Name -in @('bin','obj') };" ^
  "if ($targets.Count -eq 0) { Write-Host 'Nothing to remove.'; exit 0 };" ^
  "$freed = 0;" ^
  "foreach ($t in $targets) {" ^
  "  if (-not (Test-Path $t.FullName)) { continue };" ^
  "  $size = (Get-ChildItem $t.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum;" ^
  "  if ($size) { $freed += $size };" ^
  "  Write-Host ('  ' + $t.FullName.Substring($root.Length).TrimStart('\'));" ^
  "  Remove-Item $t.FullName -Recurse -Force -ErrorAction SilentlyContinue;" ^
  "};" ^
  "Write-Host '';" ^
  "Write-Host ('Freed {0:N1} MB.' -f ($freed / 1MB))"

echo.
echo Rebuild with "Build Clearspace.cmd".
pause
endlocal
