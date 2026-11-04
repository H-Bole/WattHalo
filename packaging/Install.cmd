@echo off
chcp 65001 >nul
set "WATTMONITOR_SETUP_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { try { $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; $desktop=[Environment]::GetFolderPath('DesktopDirectory'); $script=Join-Path $env:WATTMONITOR_SETUP_ROOT 'Install.ps1'; $command='& '''+$script.Replace('''','''''')+''' -OwnerSid '''+$sid+''' -DesktopDirectory '''+$desktop.Replace('''','''''')+''''; $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)); $p=Start-Process -FilePath ($env:SystemRoot+'\System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -Wait -PassThru; exit $p.ExitCode } catch { Write-Host $_.Exception.Message; exit 1 } }"
if errorlevel 1 (echo 安装未完成；若取消了管理员授权，请重新运行。& pause)
