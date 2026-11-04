@echo off
chcp 65001 >nul
set "WATTMONITOR_SETUP_ROOT=%~dp0"
(
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { try { $script=Join-Path $env:WATTMONITOR_SETUP_ROOT 'Uninstall.ps1'; $command='& '''+$script.Replace('''','''''')+''''; $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)); $p=Start-Process -FilePath ($env:SystemRoot+'\System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -Wait -PassThru; exit $p.ExitCode } catch { Write-Host $_.Exception.Message; exit 1 } }"
if errorlevel 1 (echo 卸载未完成；请查看提示，退出应用后重试。& pause)
exit /b
)
