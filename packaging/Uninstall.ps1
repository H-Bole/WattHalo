[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$target=$null
$serviceExe=$null
$transcribing=$false
function Assert-NoLink([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "路径包含链接，已停止以保护文件：$current" }
        $parent=[IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ($parent -eq $current) { break }; $current=$parent
    }
}
function Test-OwnShortcut([object]$Shell, [string]$ShortcutPath, [string]$AppExe) {
    try {
        $item=Get-Item -LiteralPath $ShortcutPath -Force -ErrorAction SilentlyContinue
        if ($null -eq $item -or $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
        $targetPath=[string]$Shell.CreateShortcut($ShortcutPath).TargetPath
        if ([string]::IsNullOrWhiteSpace($targetPath) -or -not [IO.Path]::IsPathRooted($targetPath)) { return $false }
        return [string]::Equals([IO.Path]::GetFullPath($targetPath),$AppExe,[StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}
function Test-OwnUninstallEntry([string]$Path, [string]$InstallDirectory) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $key=Get-Item -LiteralPath $Path
        $marker=[string]$key.GetValue('WattMonitorProduct','')
        $location=[string]$key.GetValue('InstallLocation','')
        if ($marker -ne 'WattMonitor' -or [string]::IsNullOrWhiteSpace($location) -or -not [IO.Path]::IsPathRooted($location)) { return $false }
        return [string]::Equals([IO.Path]::GetFullPath($location).TrimEnd('\'),$InstallDirectory,[StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}
function Resolve-InstalledPath([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw '安装清单包含不安全路径。' }
    $full=[IO.Path]::GetFullPath((Join-Path $target $Relative))
    if (-not $full.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '安装清单超出产品目录，已停止卸载。' }
    Assert-NoLink $full
    return $full
}
function Resolve-HostPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw '更新助手文件清单包含不安全路径。' }
    $full=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '更新助手文件清单超出目录。' }
    Assert-NoLink $full
    return $full
}
try {
    Add-Type -AssemblyName System.Windows.Forms
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员授权，请双击 Uninstall.cmd。' }
    if (-not [Environment]::Is64BitProcess) { throw '请使用64位安装包卸载。' }
    $registryPath='HKLM:\SOFTWARE\WattMonitor'
    $registered=Get-ItemProperty -LiteralPath $registryPath -Name InstallDirectory -ErrorAction SilentlyContinue
    $candidate=if($null -ne $registered){[string]$registered.InstallDirectory}else{$PSScriptRoot}
    if ($candidate -notmatch '^[a-zA-Z]:\\' -or $candidate.Substring(2).Contains(':')) { throw '安装记录不是有效的本地绝对路径，未执行卸载。' }
    $target=[IO.Path]::GetFullPath($candidate).TrimEnd('\')
    if ((Split-Path -Leaf $target) -ne 'WattMonitor') { throw '安装目录必须以 WattMonitor 结尾；请从已安装目录运行卸载程序。' }
    $serviceExe=Join-Path $target 'service\WattMonitor.Service.exe'
    Assert-NoLink $target
    $logDir=Join-Path $env:ProgramData 'WattMonitor\Logs'; Assert-NoLink $logDir
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $log=Join-Path $logDir ('uninstall-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
    Start-Transcript -LiteralPath $log | Out-Null; $transcribing=$true
    $manifestPath=Join-Path $target 'install-manifest.json'
    Assert-NoLink $manifestPath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw '找不到有效安装清单；为保护其他文件，未执行删除。请用原安装包修复后重试。' }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Product -ne 'WattMonitor' -or $manifest.Format -ne 1) { throw '安装清单无法识别，未执行删除。' }
    if ($manifest.PSObject.Properties.Name -contains 'InstallDirectory' -and -not [string]::Equals([IO.Path]::GetFullPath([string]$manifest.InstallDirectory).TrimEnd('\'),$target,[StringComparison]::OrdinalIgnoreCase)) { throw '安装清单与注册记录目录不一致，未执行删除。' }
    if ((Test-Path -LiteralPath $registryPath) -and @(Get-ChildItem -LiteralPath $registryPath).Count) { throw '产品注册项含有未知子项，未执行卸载。' }
    $owner=[string]$manifest.OwnerSid
    if ($owner -notmatch '^S-\d+(?:-\d+)+$') { throw '安装清单SID无效，未执行卸载。' }
    $warnings=@()
    $desktopDirectories=@([string]$manifest.DesktopDirectory)
    if ($manifest.PSObject.Properties.Name -contains 'DesktopDirectories') { $desktopDirectories+=@($manifest.DesktopDirectories) }
    $shortcutPaths=@()
    foreach ($desktop in @($desktopDirectories | Sort-Object -Unique)) {
        try {
            if ([string]::IsNullOrWhiteSpace([string]$desktop) -or -not [IO.Path]::IsPathRooted([string]$desktop)) { throw '无效桌面路径' }
            $desktop=[IO.Path]::GetFullPath([string]$desktop)
            Assert-NoLink $desktop
            foreach ($name in @('能耗监测.lnk','WattMonitor.lnk','WattHalo.lnk')) { $shortcutPaths+=@(Join-Path $desktop $name) }
        } catch { $warnings+=@('旧桌面路径不可安全访问，已保留其中的快捷方式。') }
    }
    $paths=@($manifest.Files | ForEach-Object { Resolve-InstalledPath ([string]$_) } | Sort-Object -Unique)
    foreach ($file in $paths) { if ((Test-Path -LiteralPath $file) -and -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "清单中的文件变成了目录，已停止：$file" } }
    $updateHost=Join-Path (Split-Path -Parent $target) 'WattMonitor.UpdateHost'
    $hostFiles=@(); $hostDirectories=@(); $ownHost=$false
    Assert-NoLink $updateHost
    if (Test-Path -LiteralPath $updateHost -PathType Container) {
        $hostRecord=Join-Path $updateHost 'host-manifest.json'
        Assert-NoLink $hostRecord
        if (Test-Path -LiteralPath $hostRecord -PathType Leaf) {
            $hostInfo=Get-Content -LiteralPath $hostRecord -Raw | ConvertFrom-Json
            $ownHost=($hostInfo.Product -eq 'WattMonitor' -and $hostInfo.Format -eq 1 -and [string]::Equals([string]$hostInfo.InstallDirectory,$target,[StringComparison]::OrdinalIgnoreCase))
        }
        if ($ownHost) {
            if ((Test-Path -LiteralPath (Join-Path $updateHost 'transaction')) -or (Test-Path -LiteralPath (Join-Path $updateHost 'cleanup'))) { throw '存在未完成的更新恢复或清理，不能卸载；请先重启电脑。' }
            foreach ($process in @(Get-Process -Name 'WattMonitor.Updater' -ErrorAction SilentlyContinue)) {
                $runningPath=$null; try { $runningPath=$process.Path } catch { }
                if ([string]::IsNullOrWhiteSpace($runningPath)) { throw '无法确认运行中更新助手的位置，请稍后重试。' }
                if ($runningPath.StartsWith($updateHost+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '更新助手仍在运行，请稍后重试。' }
            }
            $versions=Join-Path $updateHost 'versions'; Assert-NoLink $versions
            if (Test-Path -LiteralPath $versions -PathType Container) {
                foreach ($slot in @(Get-ChildItem -LiteralPath $versions -Directory)) {
                    Assert-NoLink $slot.FullName
                    if ($slot.Name -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') { continue }
                    $slotRecord=Join-Path $slot.FullName 'slot-files.json'; Assert-NoLink $slotRecord
                    if (-not (Test-Path -LiteralPath $slotRecord -PathType Leaf)) { continue }
                    $slotInfo=Get-Content -LiteralPath $slotRecord -Raw | ConvertFrom-Json
                    if ($slotInfo.Product -ne 'WattMonitor' -or $slotInfo.Format -ne 1) { continue }
                    foreach ($relative in @($slotInfo.Files)) {
                        $file=Resolve-HostPath $slot.FullName ([string]$relative)
                        if ((Test-Path -LiteralPath $file) -and -not (Test-Path -LiteralPath $file -PathType Leaf)) { throw '更新助手清单文件已变成目录。' }
                        $hostFiles+=@($file)
                        $dir=Split-Path -Parent $file
                        while ($dir -and -not [string]::Equals($dir,$slot.FullName,[StringComparison]::OrdinalIgnoreCase)) { $hostDirectories+=@($dir); $dir=Split-Path -Parent $dir }
                    }
                    $hostFiles+=@($slotRecord); $hostDirectories+=@($slot.FullName)
                }
                $hostDirectories+=@($versions)
            }
        } else { $warnings+=@('同名更新助手目录归属不明，已保留。') }
    }
    foreach ($app in @(Get-Process -Name 'WattMonitor.App' -ErrorAction SilentlyContinue)) {
        $appPath=$null
        try { $appPath=$app.Path } catch { }
        if ([string]::IsNullOrWhiteSpace($appPath)) { throw '无法确认正在运行的能耗监测程序位置，请退出该程序后重试。' }
        if ([string]::Equals($appPath,(Join-Path $target 'WattMonitor.App.exe'),[StringComparison]::OrdinalIgnoreCase)) { throw '请先从任务栏图标菜单退出已安装的能耗监测，再重新卸载。安装器不会强行结束应用。' }
    }
    $service=Get-CimInstance -ClassName Win32_Service -Filter "Name='WattMonitor'"
    if ($null -ne $service) {
        if ($service.PathName -notmatch '^"([^\"]+)"(?:\s|$)' -or -not [string]::Equals($matches[1],$serviceExe,[StringComparison]::OrdinalIgnoreCase)) { throw '同名服务不属于本产品安装目录，未停止或删除。' }
        if ($service.State -ne 'Stopped') {
            $controller=Get-Service -Name WattMonitor
            try { Stop-Service -InputObject $controller; $controller.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
            finally { $controller.Dispose() }
        }
        $result=Invoke-CimMethod -InputObject $service -MethodName Delete
        if ($result.ReturnValue -ne 0) { throw "服务移除失败，Windows代码：$($result.ReturnValue)" }
    }
    $shell=New-Object -ComObject WScript.Shell
    $appExe=Join-Path $target 'WattMonitor.App.exe'
    foreach ($shortcutPath in $shortcutPaths) {
        if (Test-OwnShortcut $shell $shortcutPath $appExe) {
            try { Remove-Item -LiteralPath $shortcutPath } catch { $warnings+=@('部分本应用快捷方式未能移除。') }
        }
    }
    $runPath='Registry::HKEY_USERS\'+$owner+'\Software\Microsoft\Windows\CurrentVersion\Run'
    if (Test-Path -LiteralPath $runPath) {
        $key=Get-Item -LiteralPath $runPath
        $value=$key.GetValue('WattMonitor',$null)
        $expected='"'+$appExe+'" --background'
        if ($null -ne $value -and [string]::Equals(([string]$value).Trim(),$expected,[StringComparison]::OrdinalIgnoreCase)) { Remove-ItemProperty -LiteralPath $runPath -Name 'WattMonitor' }
    }
    $directories=@($paths | ForEach-Object {
        $dir=Split-Path -Parent $_
        while ($dir -and -not [string]::Equals($dir,$target,[StringComparison]::OrdinalIgnoreCase)) { $dir; $dir=Split-Path -Parent $dir }
    } | Sort-Object -Unique | Sort-Object Length -Descending)
    foreach ($file in $paths) { if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file } }
    if ($ownHost) {
        foreach ($file in @($hostFiles | Sort-Object -Unique)) { if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file } }
        foreach ($dir in @($hostDirectories | Sort-Object -Unique | Sort-Object Length -Descending)) {
            Assert-NoLink $dir
            if ((Test-Path -LiteralPath $dir -PathType Container) -and @(Get-ChildItem -LiteralPath $dir).Count -eq 0) { Remove-Item -LiteralPath $dir }
        }
        foreach ($name in @('active.txt','active.pending','update.lock')) {
            $file=Join-Path $updateHost $name; Assert-NoLink $file
            if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
        }
        $remaining=@(Get-ChildItem -LiteralPath $updateHost | Where-Object { $_.Name -ne 'host-manifest.json' })
        if ($remaining.Count -eq 0) { Remove-Item -LiteralPath (Join-Path $updateHost 'host-manifest.json'); Remove-Item -LiteralPath $updateHost }
        else { $warnings+=@('更新助手目录包含未知文件，已保留。') }
    }
    foreach ($dir in $directories) {
        Assert-NoLink $dir
        if ((Test-Path -LiteralPath $dir -PathType Container) -and @(Get-ChildItem -LiteralPath $dir).Count -eq 0) { Remove-Item -LiteralPath $dir }
    }
    Remove-Item -LiteralPath $manifestPath
    if (@(Get-ChildItem -LiteralPath $target).Count -eq 0) { Remove-Item -LiteralPath $target }
    if (Test-Path -LiteralPath $registryPath) { Remove-Item -LiteralPath $registryPath }
    $uninstallKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WattMonitor'
    if (Test-OwnUninstallEntry $uninstallKey $target) {
        $key=Get-Item -LiteralPath $uninstallKey
        foreach ($name in @('DisplayName','DisplayVersion','Publisher','DisplayIcon','UninstallString','NoModify','NoRepair','InstallLocation','WattMonitorProduct')) {
            if ($key.GetValueNames() -contains $name) { Remove-ItemProperty -LiteralPath $uninstallKey -Name $name }
        }
        $key=Get-Item -LiteralPath $uninstallKey
        if ($key.GetValueNames().Count -eq 0 -and @(Get-ChildItem -LiteralPath $uninstallKey).Count -eq 0) { Remove-Item -LiteralPath $uninstallKey }
    }
    $message="卸载完成。已保留用户历史数据、配置、安装日志和共享 PawnIO 驱动。`n日志：$log"
    if ($warnings.Count) { $message+="`n`n"+($warnings -join "`n"); foreach ($warning in $warnings) { Write-Warning $warning } }
    if (Test-Path -LiteralPath $target) { $message += "`n安装目录内存在未列入本产品清单的文件，已保留。" }
    [Windows.Forms.MessageBox]::Show($message,'WattHalo 卸载完成','OK','Information') | Out-Null
} catch {
    $message=$_.Exception.Message; Write-Error $message -ErrorAction Continue
    if ($transcribing) { $message += "`n日志：$log" }
    try { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.MessageBox]::Show($message,'WattHalo 卸载未完成','OK','Error') | Out-Null } catch { }
    exit 1
} finally { if ($transcribing) { Stop-Transcript | Out-Null } }
