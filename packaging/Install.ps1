[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OwnerSid, [string]$DesktopDirectory,
    [string]$InstallDirectory, [switch]$RepairService, [switch]$Quiet)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$target = $null
$serviceExe = $null
$transcribing = $false
$stoppedExistingService = $false
$copyStarted = $false
$pendingSlot = $null
$updateHost = $null
$updateLock = $null
$manualTransaction = $null
$manualJournal = $null
$manualCommitted = $false
$manualPrepared = $false
function Assert-NoLink([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "路径包含链接，已停止以保护文件：$current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ($parent -eq $current) { break }; $current = $parent
    }
}
function Resolve-PayloadPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw '文件清单包含不安全路径。' }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw '文件清单超出安装目录。' }
    Assert-NoLink $full
    return $full
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
function Assert-OwnService($Service) {
    if ($null -eq $Service) { return }
    if ($Service.PathName -notmatch '^"([^\"]+)"(?:\s|$)' -or -not [string]::Equals($matches[1], $serviceExe, [StringComparison]::OrdinalIgnoreCase)) { throw '同名 WattMonitor 服务属于其他安装位置，安装器不会停止或覆盖它。' }
}
function Assert-ProtectedDirectory([string]$Path) {
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $root=[IO.Path]::GetPathRoot($Path)
    $current=$Path
    $writeMask=[long][Security.AccessControl.FileSystemRights]::Write -bor [long][Security.AccessControl.FileSystemRights]::Delete -bor [long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor 0x40000000 -bor 0x10000000
    $rootMask=[long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor 0x10000000
    while ($current) {
        $isRoot=[string]::Equals($current.TrimEnd('\'),$root.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)
        $acl=Get-Acl -LiteralPath $current
        if ($trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "安装父目录所有者不是受信任系统账户，不能在此运行高权限服务：$current" }
        $mask=if ($isRoot) { $rootMask } else { $writeMask }
        foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -or (([long]$rule.FileSystemRights -band $mask) -eq 0)) { continue }
            if ($trusted -contains $rule.IdentityReference.Value) { continue }
            throw "目录允许非管理员替换或修改安装路径，不能在此运行LocalSystem服务：$current"
        }
        if ($isRoot) { break }
        $current=Split-Path -Parent $current
    }
}
function Assert-ProtectedFile([string]$Path) {
    Assert-NoLink $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "找不到需要校验的安装文件：$Path" }
    $trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $mask=[long][Security.AccessControl.FileSystemRights]::Write -bor [long][Security.AccessControl.FileSystemRights]::Delete -bor [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor 0x40000000 -bor 0x10000000
    $acl=Get-Acl -LiteralPath $Path
    if ($trusted -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "安装文件所有者不受信任：$Path" }
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($trusted -notcontains $rule.IdentityReference.Value) -and (([long]$rule.FileSystemRights -band $mask) -ne 0)) { throw "安装文件允许非管理员修改：$Path" }
    }
}
function Assert-RepairInstallation {
    if (-not [string]::Equals([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'),$target,[StringComparison]::OrdinalIgnoreCase)) { throw '请从当前安装目录运行采集服务修复。' }
    $manifestPath=Join-Path $target 'install-manifest.json'
    $payloadPath=Join-Path $target 'payload-manifest.json'
    Assert-ProtectedFile $manifestPath; Assert-ProtectedFile $payloadPath
    $installed=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($installed.Product -ne 'WattMonitor' -or $installed.Format -ne 1 -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$installed.InstallDirectory).TrimEnd('\'),$target,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$installed.OwnerSid,$sid.Value,[StringComparison]::OrdinalIgnoreCase)) { throw '安装记录与当前目录或用户不一致，未修复服务。' }
    $owned=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @($installed.Files)) {
        if (-not $owned.Add((Resolve-PayloadPath $target ([string]$relative)))) { throw '安装记录包含重复路径，未修复服务。' }
    }
    if (-not $owned.Contains($payloadPath)) { throw '安装记录缺少发布清单，未修复服务。' }
    $payload=Get-Content -LiteralPath $payloadPath -Raw | ConvertFrom-Json
    if ($payload.Product -ne 'WattMonitor' -or $payload.Format -ne 1) { throw '发布文件清单无效，未修复服务。' }
    $paths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $serviceFiles=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $serviceRoot=Join-Path $target 'service'
    foreach ($entry in @($payload.Files)) {
        $path=Resolve-PayloadPath $target ([string]$entry.Path)
        if (-not $paths.Add($path) -or -not $owned.Contains($path) -or [string]$entry.Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw '发布清单包含无效文件或未登记文件，未修复服务。' }
        if ($path.StartsWith($serviceRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { $null=$serviceFiles.Add($path) }
        if ($path.StartsWith($serviceRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $path -ieq (Join-Path $target 'WattMonitor.App.exe') -or $path -ieq (Join-Path $target 'Install.ps1')) {
            Assert-ProtectedDirectory (Split-Path -Parent $path)
            Assert-ProtectedFile $path
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$entry.Sha256) { throw "安装文件校验失败，未修复服务：$path" }
        }
    }
    foreach ($required in @($serviceExe,(Join-Path $target 'WattMonitor.App.exe'),(Join-Path $target 'Install.ps1'))) {
        if (-not $paths.Contains($required)) { throw "发布清单缺少必要文件，未修复服务：$required" }
    }
    $directories=New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($serviceRoot)
    while ($directories.Count) {
        $directory=$directories.Pop()
        Assert-NoLink $directory; Assert-ProtectedDirectory $directory
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
            Assert-NoLink $item.FullName
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            elseif (-not $serviceFiles.Contains($item.FullName)) { throw "服务目录存在未登记文件，未修复服务：$($item.FullName)" }
        }
    }
}
function New-ProtectedProductDirectory([string]$Path) {
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $admin=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($admin)
    foreach ($identity in @('S-1-5-18','S-1-5-32-544','S-1-5-32-545')) {
        $sid=New-Object Security.Principal.SecurityIdentifier($identity)
        $rights=if($identity -eq 'S-1-5-32-545'){[Security.AccessControl.FileSystemRights]::ReadAndExecute}else{[Security.AccessControl.FileSystemRights]::FullControl}
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,$rights,([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    if (Test-Path -LiteralPath $Path) { Set-Acl -LiteralPath $Path -AclObject $acl }
    else { [IO.Directory]::CreateDirectory($Path,$acl) | Out-Null }
}
function Get-FileHashOrNull([string]$Path) {
    Assert-NoLink $Path
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "文件路径已变为目录：$Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Write-DurableFile([string]$Path, [byte[]]$Bytes) {
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Copy-DurableFile([string]$From, [string]$To) {
    [IO.File]::Copy($From,$To,$false)
    $stream=New-Object IO.FileStream($To,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { $stream.Flush($true) } finally { $stream.Dispose() }
}
function Get-BytesHash([byte[]]$Bytes) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-','') } finally { $algorithm.Dispose() }
}
function Assert-ManualJournal($Journal) {
    if ($Journal.Product -ne 'WattMonitor' -or $Journal.Format -ne 2 -or $Journal.Kind -ne 'ManualInstall' -or
        -not [string]::Equals([string]$Journal.InstallDirectory,$target,[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Journal.OwnerSid,$sid.Value,[StringComparison]::OrdinalIgnoreCase) -or
        $Journal.StartMode -notin @('Auto','Manual','Disabled') -or $Journal.WasRunning -isnot [bool] -or
        [string]$Journal.ActiveAfterHash -notmatch '^[0-9A-F]{64}$' -or
        ($null -ne $Journal.ActiveBeforeHash -and [string]$Journal.ActiveBeforeHash -notmatch '^[0-9A-F]{64}$')) { throw '手动安装恢复记录无效，已保留备份。' }
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($Journal.Files)) {
        $path=Resolve-PayloadPath $target ([string]$entry.Path)
        if (-not $seen.Add($path) -or [string]$entry.AfterHash -notmatch '^[0-9A-F]{64}$' -or
            ($null -ne $entry.BeforeHash -and [string]$entry.BeforeHash -notmatch '^[0-9A-F]{64}$')) { throw '手动安装恢复记录包含无效文件，已保留备份。' }
    }
    foreach ($required in @('install-manifest.json','payload-manifest.json','WattMonitor.App.exe','service\WattMonitor.Service.exe')) {
        if (-not $seen.Contains((Resolve-PayloadPath $target $required))) { throw '手动安装恢复记录不完整，已保留备份。' }
    }
}
function Assert-ManualService {
    $current=Get-CimInstance -ClassName Win32_Service -Filter "Name='WattMonitor'"
    if ($null -eq $current -or -not [string]::Equals([string]$current.PathName,('"'+$serviceExe+'" --owner-sid '+$sid.Value),[StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$current.StartName,'LocalSystem',[StringComparison]::OrdinalIgnoreCase)) { throw '采集服务配置与原安装记录不一致，已保留原文件。' }
    return $current
}
function Set-ManualServiceMode([string]$Mode) {
    $current=Assert-ManualService
    $modeName=if ($Mode -eq 'Auto') { 'Automatic' } else { $Mode }
    $result=Invoke-CimMethod -InputObject $current -MethodName ChangeStartMode -Arguments @{ StartMode=$modeName }
    if ($result.ReturnValue -ne 0) { throw "恢复采集服务启动方式失败，Windows代码：$($result.ReturnValue)" }
}
function Stop-ManualService {
    $current=Assert-ManualService
    if ($current.State -ne 'Stopped') {
        $controller=Get-Service -Name WattMonitor
        try { Stop-Service -InputObject $controller; $controller.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
        finally { $controller.Dispose() }
    }
}
function Restore-ManualService($Journal) {
    $mode=if ($Journal.WasRunning -and $Journal.StartMode -eq 'Disabled') { 'Manual' } else { [string]$Journal.StartMode }
    Set-ManualServiceMode $mode
    if ($Journal.WasRunning) {
        Start-Service -Name WattMonitor
        $controller=Get-Service -Name WattMonitor
        try { $controller.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) } finally { $controller.Dispose() }
        if ($Journal.StartMode -eq 'Disabled') { Set-ManualServiceMode 'Disabled' }
    }
}
function Replace-ManualFile([string]$Destination, [string]$Staged, $BeforeHash, [string]$AfterHash) {
    if ((Get-FileHashOrNull $Destination) -ne $BeforeHash -or (Get-FileHashOrNull $Staged) -ne $AfterHash) { throw "安装文件在切换前发生变化：$Destination" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Assert-ProtectedDirectory (Split-Path -Parent $Destination)
    if ($null -eq $BeforeHash) { [IO.File]::Move($Staged,$Destination) }
    else { [IO.File]::Replace($Staged,$Destination,[System.Management.Automation.Language.NullString]::Value) }
}
function Restore-ManualFile([string]$Destination, [string]$Backup, [string]$Temporary, $BeforeHash, [string]$AfterHash) {
    $currentHash=Get-FileHashOrNull $Destination
    if ($currentHash -eq $BeforeHash) { return }
    if ($null -ne $currentHash -and $currentHash -ne $AfterHash) { throw "文件已被其他操作修改，未覆盖：$Destination" }
    if ($null -eq $BeforeHash) {
        if ($null -ne $currentHash) { Remove-Item -LiteralPath $Destination }
        return
    }
    if ((Get-FileHashOrNull $Backup) -ne $BeforeHash) { throw "原文件备份缺失或损坏：$Backup" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Temporary) -Force | Out-Null
    if (Test-Path -LiteralPath $Temporary) { Remove-Item -LiteralPath $Temporary }
    Copy-DurableFile $Backup $Temporary
    if ((Get-FileHashOrNull $Temporary) -ne $BeforeHash) { throw '恢复文件复制校验失败。' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Assert-ProtectedDirectory (Split-Path -Parent $Destination)
    if ($null -eq $currentHash) { [IO.File]::Move($Temporary,$Destination) }
    else { [IO.File]::Replace($Temporary,$Destination,[System.Management.Automation.Language.NullString]::Value) }
}
function Clear-ManualTransaction([string]$Path, $Journal) {
    $files=@('armed','commit-ready','committed','active-before','active-after','active-restore')
    foreach ($entry in @($Journal.Files)) {
        foreach ($part in @('before','after','restore')) { $files+=@((Join-Path $part ([string]$entry.Path))) }
    }
    $directories=@()
    foreach ($relative in $files) {
        $file=Resolve-PayloadPath $Path $relative
        if (Test-Path -LiteralPath $file) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "事务文件已变为目录：$file" }
            Remove-Item -LiteralPath $file
        }
        $directory=Split-Path -Parent $file
        while ($directory -and -not [string]::Equals($directory,$Path,[StringComparison]::OrdinalIgnoreCase)) { $directories+=@($directory); $directory=Split-Path -Parent $directory }
    }
    foreach ($directory in @($directories | Sort-Object -Unique | Sort-Object Length -Descending)) {
        Assert-NoLink $directory
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
    }
    if (@(Get-ChildItem -LiteralPath $Path -Force | Where-Object { $_.Name -ne 'journal.json' }).Count) { throw "事务目录包含未知文件，已保留：$Path" }
    Remove-Item -LiteralPath (Join-Path $Path 'journal.json')
    Remove-Item -LiteralPath $Path
}
function Finish-ManualTransaction([string]$Path, $Journal) {
    # Rename before deleting anything so an interrupted cleanup can never be mistaken for a rollback.
    $cleanup=Join-Path $updateHost ('transaction-cleanup-'+[guid]::NewGuid().ToString('N'))
    [IO.Directory]::Move($Path,$cleanup)
    Clear-ManualTransaction $cleanup $Journal
}
function Recover-ManualTransaction([string]$Path, $Journal) {
    Assert-ManualJournal $Journal
    if (-not (Test-Path -LiteralPath (Join-Path $Path 'committed')) -and (Test-Path -LiteralPath (Join-Path $Path 'armed'))) {
        Set-ManualServiceMode 'Disabled'
        Stop-ManualService
        foreach ($entry in @($Journal.Files)) {
            $relative=[string]$entry.Path
            Restore-ManualFile (Resolve-PayloadPath $target $relative) (Resolve-PayloadPath (Join-Path $Path 'before') $relative) (Resolve-PayloadPath (Join-Path $Path 'restore') $relative) $entry.BeforeHash ([string]$entry.AfterHash)
        }
        Restore-ManualFile (Join-Path $updateHost 'active.txt') (Join-Path $Path 'active-before') (Join-Path $Path 'active-restore') $Journal.ActiveBeforeHash ([string]$Journal.ActiveAfterHash)
        Restore-ManualService $Journal
    }
    Finish-ManualTransaction $Path $Journal
}
try {
    Add-Type -AssemblyName System.Windows.Forms
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '需要管理员授权。请双击 Install.cmd。' }
    if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) { throw '此安装包需要 64 位 Windows 11。' }
    $registered=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WattMonitor' -Name InstallDirectory -ErrorAction SilentlyContinue
    if ($RepairService -and $null -eq $registered) { throw '找不到现有安装的注册位置，未修复服务。' }
    if (-not $PSBoundParameters.ContainsKey('InstallDirectory')) {
        if ($null -ne $registered) { $InstallDirectory=[string]$registered.InstallDirectory }
        else {
            $programFiles=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            if ([string]::IsNullOrWhiteSpace($programFiles)) { throw '无法确定系统程序目录，请明确指定安装目录。' }
            $InstallDirectory=Join-Path $programFiles 'WattMonitor'
        }
    }
    if ($InstallDirectory -notmatch '^[a-zA-Z]:\\' -or $InstallDirectory.Substring(2).Contains(':')) { throw '安装目录必须是本地磁盘上的绝对路径。' }
    $target=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    if ((Split-Path -Leaf $target) -ne 'WattMonitor') { throw '安装目录最后一级必须为 WattMonitor。' }
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($target))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw '采集服务只允许安装到本地固定磁盘。' }
    $parentDirectory=Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parentDirectory -PathType Container)) { throw '安装父目录必须已存在；安装器不会修改其他软件目录。' }
    Assert-NoLink $target
    Assert-ProtectedDirectory $parentDirectory
    if (Test-Path -LiteralPath $target) { Assert-ProtectedDirectory $target }
    $updateHost=Join-Path $parentDirectory 'WattMonitor.UpdateHost'
    Assert-NoLink $updateHost
    if (Test-Path -LiteralPath $updateHost) {
        Assert-ProtectedDirectory $updateHost
        $hostRecord=Join-Path $updateHost 'host-manifest.json'
        Assert-NoLink $hostRecord
        if (-not (Test-Path -LiteralPath $hostRecord -PathType Leaf)) { throw '更新助手目录已存在但无法确认归属，未覆盖。' }
        $hostInfo=Get-Content -LiteralPath $hostRecord -Raw | ConvertFrom-Json
        if ($hostInfo.Product -ne 'WattMonitor' -or $hostInfo.Format -ne 1 -or -not [string]::Equals([string]$hostInfo.InstallDirectory,$target,[StringComparison]::OrdinalIgnoreCase)) { throw '更新助手目录属于其他安装，未覆盖。' }
        if ($RepairService) { Assert-ProtectedFile $hostRecord }
        if (Test-Path -LiteralPath (Join-Path $updateHost 'cleanup')) { throw '检测到尚未完成清理的更新事务，请先重启电脑。' }
    }
    $serviceExe=Join-Path $target 'service\WattMonitor.Service.exe'
    if ($null -ne $registered -and -not [string]::Equals([IO.Path]::GetFullPath($registered.InstallDirectory).TrimEnd('\'),$target,[StringComparison]::OrdinalIgnoreCase)) { throw '检测到其他目录中的现有安装，请先用该目录的 Uninstall.cmd 卸载，再更换安装目录；历史数据将保留。' }
    if ($OwnerSid -notmatch '^S-\d+(?:-\d+)+$') { throw '原始用户SID无效。' }
    $sid = New-Object Security.Principal.SecurityIdentifier($OwnerSid)
    if (-not (Test-Path -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\'+$sid.Value))) { throw '找不到原始用户配置，请从需要使用软件的用户账户双击 Install.cmd。' }
    if ($RepairService) {
        if (-not (Test-Path -LiteralPath $target -PathType Container) -or -not (Test-Path -LiteralPath $updateHost -PathType Container)) { throw '现有安装或更新助手目录不存在，未修复服务。' }
        $lockPath=Join-Path $updateHost 'update.lock'
        Assert-NoLink $lockPath
        if (Test-Path -LiteralPath $lockPath) { Assert-ProtectedFile $lockPath }
        $updateLock=New-Object IO.FileStream($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        foreach ($name in @('transaction','cleanup')) {
            $pending=Join-Path $updateHost $name; Assert-NoLink $pending
            if (Test-Path -LiteralPath $pending) { throw '存在尚未完成的更新事务，请先恢复后再修复采集服务。' }
        }
        Assert-RepairInstallation
        $service=Assert-ManualService
        if ($service.ServiceType -ne 'Own Process' -or $service.StartMode -notin @('Auto','Manual','Disabled') -or $service.State -notin @('Running','Stopped')) { throw '采集服务类型或状态无法安全确认，请稍后重试。' }
        if ($service.StartMode -eq 'Disabled') { throw '采集服务已被禁用，请先在 Windows 服务设置中启用，再重试修复。' }
        $originalMode=[string]$service.StartMode; $wasRunning=($service.State -eq 'Running')
        try {
            $current=Assert-ManualService
            if ($current.StartMode -ne $originalMode -or $current.State -ne $service.State) { throw '采集服务状态已变化，未继续修复。' }
            if ($wasRunning) { Stop-ManualService }
            if ((Assert-ManualService).StartMode -ne $originalMode) { throw '采集服务启动方式已变化，未继续修复。' }
            Start-Service -Name WattMonitor
            $controller=Get-Service -Name WattMonitor
            try { $controller.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) } finally { $controller.Dispose() }
            $current=Assert-ManualService
            if ($current.State -ne 'Running' -or $current.StartMode -ne $originalMode) { throw '采集服务运行状态或启动方式已变化。' }
        } catch {
            $repairFailure=$_
            if ($wasRunning) {
                try {
                    $current=Assert-ManualService
                    if ($current.State -eq 'Stopped' -and $current.StartMode -eq $originalMode) {
                        Start-Service -Name WattMonitor
                        $controller=Get-Service -Name WattMonitor
                        try { $controller.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) } finally { $controller.Dispose() }
                    }
                } catch { }
            }
            throw $repairFailure
        }
        $message='采集服务修复完成。'
        if ($Quiet) { Write-Output $message }
        else { [Windows.Forms.MessageBox]::Show($message,'WattHalo 采集服务','OK','Information') | Out-Null }
        return
    }
    if ([string]::IsNullOrWhiteSpace($DesktopDirectory)) { throw '安装需要原始用户桌面目录。' }
    $DesktopDirectory = [IO.Path]::GetFullPath($DesktopDirectory)
    Assert-NoLink $DesktopDirectory
    if (-not (Test-Path -LiteralPath $DesktopDirectory -PathType Container)) { throw '原始用户桌面目录不可用。' }
    Assert-NoLink $target
    $logDir = Join-Path $env:ProgramData 'WattMonitor\Logs'
    Assert-NoLink $logDir
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $warnings=@()
    $log = Join-Path $logDir ('install-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')
    Start-Transcript -LiteralPath $log | Out-Null; $transcribing=$true
    if (-not (Test-Path -LiteralPath $updateHost)) {
        New-ProtectedProductDirectory $updateHost
        $hostBytes=[Text.Encoding]::UTF8.GetBytes(([ordered]@{ Product='WattMonitor'; Format=1; InstallDirectory=$target } | ConvertTo-Json))
        Write-DurableFile (Join-Path $updateHost 'host-manifest.json') $hostBytes
    }
    $lockPath=Join-Path $updateHost 'update.lock'
    Assert-NoLink $lockPath
    $updateLock=New-Object IO.FileStream($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if (Test-Path -LiteralPath (Join-Path $updateHost 'cleanup')) { throw '检测到尚未完成清理的更新事务，请先重启电脑。' }
    $transaction=Join-Path $updateHost 'transaction'
    Assert-NoLink $transaction
    if (Test-Path -LiteralPath $transaction) {
        Assert-ProtectedDirectory $transaction
        $journalPath=Join-Path $transaction 'journal.json'
        Assert-NoLink $journalPath
        if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) { throw '检测到无法识别的更新事务，请先重启电脑。' }
        $recoveryJournal=Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
        if ($recoveryJournal.Product -ne 'WattMonitor' -or $recoveryJournal.Format -ne 2 -or $recoveryJournal.Kind -ne 'ManualInstall') { throw '检测到尚未恢复的自动更新事务，请先重启电脑。' }
        Recover-ManualTransaction $transaction $recoveryJournal
        $warnings+=@('已恢复上次中断的手动安装事务。')
    }
    foreach ($cleanup in @(Get-ChildItem -LiteralPath $updateHost -Directory -Filter 'transaction-cleanup-*')) {
        try {
            if ($cleanup.Name -notmatch '^transaction-cleanup-[0-9a-f]{32}$') { continue }
            Assert-NoLink $cleanup.FullName; Assert-ProtectedDirectory $cleanup.FullName
            $cleanupJournal=Get-Content -LiteralPath (Join-Path $cleanup.FullName 'journal.json') -Raw | ConvertFrom-Json
            Assert-ManualJournal $cleanupJournal
            Clear-ManualTransaction $cleanup.FullName $cleanupJournal
        } catch { $warnings+=@("未能清理已完成的安装备份：$($cleanup.FullName)") }
    }
    $source = [IO.Path]::GetFullPath($PSScriptRoot)
    Assert-NoLink $source
    $manifestPath = Join-Path $target 'install-manifest.json'
    Assert-NoLink $manifestPath
    $pendingManifest = Join-Path $target 'install-manifest.pending'
    Assert-NoLink $pendingManifest
    if (Test-Path -LiteralPath $pendingManifest) { throw '检测到旧安装器中断时留下的清单，无法确认文件是否完整；未修改原安装，请检查安装日志。' }
    $old = $null
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $old = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($old.Product -ne 'WattMonitor' -or $old.Format -ne 1) { throw '已安装文件清单无法识别。' }
        if (-not ($old.PSObject.Properties.Name -contains 'OwnerSid') -or -not [string]::Equals([string]$old.OwnerSid,$sid.Value,[StringComparison]::OrdinalIgnoreCase)) { throw '请使用原安装用户运行升级，以保留该用户的采集权限和开机启动设置。' }
        if (($old.PSObject.Properties.Name -contains 'InstallDirectory') -and -not [string]::Equals([IO.Path]::GetFullPath([string]$old.InstallDirectory).TrimEnd('\'),$target,[StringComparison]::OrdinalIgnoreCase)) { throw '安装清单与安装目录不一致，未覆盖。' }
        foreach ($file in @($old.Files)) { $null = Resolve-PayloadPath $target $file }
    } elseif ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target).Count) { throw '目标目录包含未知文件，安装器不会覆盖。' }
    $desktopDirectories=@($DesktopDirectory)
    if ($null -ne $old) {
        $previousDesktops=@([string]$old.DesktopDirectory)
        if ($old.PSObject.Properties.Name -contains 'DesktopDirectories') { $previousDesktops+=@($old.DesktopDirectories) }
        foreach ($previous in $previousDesktops) {
            try {
                if ([string]::IsNullOrWhiteSpace([string]$previous) -or -not [IO.Path]::IsPathRooted([string]$previous)) { throw '无效桌面路径' }
                $previous=[IO.Path]::GetFullPath([string]$previous)
                Assert-NoLink $previous
                $desktopDirectories+=@($previous)
            } catch { $warnings+=@('旧桌面路径不可安全访问，已保留原有快捷方式。') }
        }
    }
    $desktopDirectories=@($desktopDirectories | Sort-Object -Unique)
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='WattMonitor'"
    Assert-OwnService $service
    if ($null -eq $old -and ($null -ne $service -or $null -ne $registered)) { throw '存在不完整的安装记录，无法安全覆盖；原文件及服务均已保留。' }
    if ($null -ne $old) {
        if ($null -eq $registered) { throw '现有安装的注册位置缺失，无法安全升级；原文件及服务均已保留。' }
        $service=Assert-ManualService
        if ($service.State -notin @('Running','Stopped') -or $service.StartMode -notin @('Auto','Manual','Disabled')) { throw '采集服务正在切换状态或启动方式无法识别，请稍后重试。' }
    } else {
        $softwareKey=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE',$true)
        if ($null -eq $softwareKey) { throw '没有写入系统安装注册表的权限，未修改安装目录。' }
        $softwareKey.Dispose()
    }
    $sameSource = [string]::Equals($source,$target,[StringComparison]::OrdinalIgnoreCase)
    Assert-NoLink (Join-Path $source 'payload-manifest.json')
    $payload = Get-Content -LiteralPath (Join-Path $source 'payload-manifest.json') -Raw | ConvertFrom-Json
    if ($payload.Product -ne 'WattMonitor' -or $payload.Format -ne 1) { throw '发布清单无法识别，请重新解压完整安装包。' }
    $files = @($payload.Files | ForEach-Object { [string]$_.Path })
    foreach ($required in @('WattMonitor.App.exe','service\WattMonitor.Service.exe','updater\WattMonitor.Updater.exe','Uninstall.ps1','Uninstall.cmd')) { if ($files -notcontains $required) { throw "发布包缺少 $required" } }
    $payloadPaths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $payload.Files) {
        $file = Resolve-PayloadPath $source $entry.Path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "发布文件缺失或损坏：$($entry.Path)" }
        $destination=Resolve-PayloadPath $target $entry.Path
        if (-not $payloadPaths.Add($destination) -or @('install-manifest.json','install-manifest.pending','payload-manifest.json') -contains $destination.Substring($target.Length+1)) { throw '发布清单包含重复或保留的文件名。' }
    }
    $fileVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'WattMonitor.App.exe'))
    $version='{0}.{1}.{2}' -f $fileVersion.FileMajorPart,$fileVersion.FileMinorPart,$fileVersion.FileBuildPart
    if ($version -eq '0.0.0') { throw '发布包缺少可识别的应用版本。' }
    if (($payload.PSObject.Properties.Name -contains 'Version') -and [string]$payload.Version -ne $version) { throw '发布清单和应用版本不一致。' }
    if (-not $sameSource -or $null -ne $old) {
        foreach ($app in @(Get-Process -Name 'WattMonitor.App' -ErrorAction SilentlyContinue)) {
            $appPath=$null
            try { $appPath=$app.Path } catch { }
            if ([string]::IsNullOrWhiteSpace($appPath)) { throw '无法确认正在运行的能耗监测程序位置，请退出该程序后重试。' }
            if ([string]::Equals($appPath,(Join-Path $target 'WattMonitor.App.exe'),[StringComparison]::OrdinalIgnoreCase)) { throw '请先从任务栏图标菜单退出已安装的能耗监测，然后重新运行安装器。安装器不会强行结束应用。' }
        }
    }
    $allFiles = @(@($files) + @('payload-manifest.json') + $(if ($null -ne $old) { @($old.Files) }) | Sort-Object -Unique)
    $installed = [ordered]@{ Product='WattMonitor'; Format=1; InstallDirectory=$target; OwnerSid=$sid.Value; DesktopDirectory=$DesktopDirectory; DesktopDirectories=$desktopDirectories; Files=$allFiles }
    if ($null -ne $old) {
        $active=Join-Path $updateHost 'active.txt'; $pendingActive=Join-Path $updateHost 'active.pending'
        Assert-NoLink $pendingActive
        if (Test-Path -LiteralPath $pendingActive) { throw '更新助手存在未确认归属的临时记录，未修改原安装。' }
        $manifestBytes=[Text.Encoding]::UTF8.GetBytes(($installed | ConvertTo-Json -Depth 4))
        $activeBytes=[Text.Encoding]::ASCII.GetBytes($version)
        $owned=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($relative in @($old.Files)) { $null=$owned.Add((Resolve-PayloadPath $target ([string]$relative))) }
        $records=@()
        foreach ($relative in @($files)+@('payload-manifest.json','install-manifest.json')) {
            $destination=Resolve-PayloadPath $target ([string]$relative)
            $beforeHash=Get-FileHashOrNull $destination
            if ($null -ne $beforeHash -and -not $owned.Contains($destination) -and -not [string]::Equals($destination,$manifestPath,[StringComparison]::OrdinalIgnoreCase)) { throw "发布文件与未登记文件冲突，未覆盖：$destination" }
            $afterHash=if ([string]::Equals($destination,$manifestPath,[StringComparison]::OrdinalIgnoreCase)) { Get-BytesHash $manifestBytes } else { Get-FileHashOrNull (Resolve-PayloadPath $source ([string]$relative)) }
            $records+=@([ordered]@{ Path=$destination.Substring($target.Length+1); BeforeHash=$beforeHash; AfterHash=$afterHash })
        }
        $manualJournal=[ordered]@{ Product='WattMonitor'; Format=2; Kind='ManualInstall'; InstallDirectory=$target; OwnerSid=$sid.Value;
            StartMode=[string]$service.StartMode; WasRunning=($service.State -eq 'Running');
            ActiveBeforeHash=(Get-FileHashOrNull $active); ActiveAfterHash=(Get-BytesHash $activeBytes); Files=$records }
        Assert-ManualJournal ([pscustomobject]$manualJournal)
        if (Test-Path -LiteralPath $transaction) { throw '检测到其他更新事务，未修改原安装。' }
        New-ProtectedProductDirectory $transaction
        $manualTransaction=$transaction; $manualPrepared=$true
        $journalBytes=[Text.Encoding]::UTF8.GetBytes(($manualJournal | ConvertTo-Json -Depth 6))
        Write-DurableFile (Join-Path $transaction 'journal.json') $journalBytes
        foreach ($entry in $records) {
            $relative=[string]$entry.Path
            if ($null -ne $entry.BeforeHash) {
                $backup=Resolve-PayloadPath (Join-Path $transaction 'before') $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
                Copy-DurableFile (Resolve-PayloadPath $target $relative) $backup
                if ((Get-FileHashOrNull $backup) -ne $entry.BeforeHash -or (Get-FileHashOrNull (Resolve-PayloadPath $target $relative)) -ne $entry.BeforeHash) { throw "原安装文件备份校验失败：$relative" }
            }
            $staged=Resolve-PayloadPath (Join-Path $transaction 'after') $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $staged) -Force | Out-Null
            if ($relative -ieq 'install-manifest.json') { Write-DurableFile $staged $manifestBytes }
            else { Copy-DurableFile (Resolve-PayloadPath $source $relative) $staged }
            if ((Get-FileHashOrNull $staged) -ne $entry.AfterHash) { throw "发布文件暂存校验失败：$relative" }
        }
        if ($null -ne $manualJournal.ActiveBeforeHash) {
            Copy-DurableFile $active (Join-Path $transaction 'active-before')
            if ((Get-FileHashOrNull (Join-Path $transaction 'active-before')) -ne $manualJournal.ActiveBeforeHash -or (Get-FileHashOrNull $active) -ne $manualJournal.ActiveBeforeHash) { throw '更新助手记录备份校验失败。' }
        }
        Write-DurableFile (Join-Path $transaction 'active-after') $activeBytes
        Write-DurableFile (Join-Path $transaction 'commit-ready') ([byte[]]@(1))
        Write-DurableFile (Join-Path $transaction 'armed') ([byte[]]@(1))
        Set-ManualServiceMode 'Disabled'
        Stop-ManualService
        $copyStarted=$true
        foreach ($entry in $records) {
            $relative=[string]$entry.Path
            Replace-ManualFile (Resolve-PayloadPath $target $relative) (Resolve-PayloadPath (Join-Path $transaction 'after') $relative) $entry.BeforeHash ([string]$entry.AfterHash)
        }
    } else {
        New-ProtectedProductDirectory $target
        # Record ownership before copying so an interrupted first installation remains identifiable.
        $installed | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $pendingManifest -Encoding UTF8
        [IO.File]::Move($pendingManifest,$manifestPath)
        if (-not $sameSource) {
            $copyStarted=$true
            foreach ($relative in @($files)+@('payload-manifest.json')) {
                $destination=Resolve-PayloadPath $target $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath (Resolve-PayloadPath $source $relative) -Destination $destination
            }
        }
    }
    foreach ($entry in $payload.Files) {
        if ((Get-FileHash -LiteralPath (Resolve-PayloadPath $target $entry.Path) -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "安装复制校验失败：$($entry.Path)。采集服务未启动，请重新解压并重试。" }
    }
    if (-not (Test-Path -LiteralPath $updateHost)) {
        New-ProtectedProductDirectory $updateHost
        [ordered]@{ Product='WattMonitor'; Format=1; InstallDirectory=$target } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $updateHost 'host-manifest.json') -Encoding UTF8
    }
    $versions=Join-Path $updateHost 'versions'
    Assert-NoLink $versions
    New-Item -ItemType Directory -Path $versions -Force | Out-Null
    $slot=Join-Path $versions $version
    Assert-NoLink $slot
    $updaterFiles=@($payload.Files | Where-Object { ([string]$_.Path).StartsWith('updater\',[StringComparison]::OrdinalIgnoreCase) })
    if (-not $updaterFiles.Count) { throw '发布包缺少更新助手文件。' }
    $pendingSlot=$null
    if (-not (Test-Path -LiteralPath $slot)) {
        $pendingSlot=Join-Path $versions ('.pending-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $pendingSlot | Out-Null
        foreach ($entry in $updaterFiles) {
            $relative=([string]$entry.Path).Substring(8)
            if ($relative -ieq 'slot-files.json') { throw '更新助手包含保留文件名。' }
            $destination=Resolve-PayloadPath $pendingSlot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Resolve-PayloadPath $target ([string]$entry.Path)) -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.Sha256) { throw '更新助手复制校验失败。' }
        }
        $slotFiles=@($updaterFiles | ForEach-Object { ([string]$_.Path).Substring(8) })
        [ordered]@{ Product='WattMonitor'; Format=1; Files=$slotFiles } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $pendingSlot 'slot-files.json') -Encoding UTF8
        Move-Item -LiteralPath $pendingSlot -Destination $slot
        $pendingSlot=$null
    } else {
        Assert-ProtectedDirectory $slot
        $slotRecord=Join-Path $slot 'slot-files.json'
        Assert-NoLink $slotRecord
        if (-not (Test-Path -LiteralPath $slotRecord -PathType Leaf)) { throw '更新助手版本槽归属不明，未覆盖。' }
        $slotInfo=Get-Content -LiteralPath $slotRecord -Raw | ConvertFrom-Json
        if ($slotInfo.Product -ne 'WattMonitor' -or $slotInfo.Format -ne 1) { throw '更新助手版本记录无效。' }
        foreach ($entry in $updaterFiles) {
            $relative=([string]$entry.Path).Substring(8)
            if (@($slotInfo.Files) -notcontains $relative -or (Get-FileHash -LiteralPath (Resolve-PayloadPath $slot $relative) -Algorithm SHA256).Hash -ne $entry.Sha256) { throw '已有更新助手版本槽与发布包不一致，未覆盖。' }
        }
        if (@($slotInfo.Files).Count -ne $updaterFiles.Count) { throw '已有更新助手版本槽包含未知记录，未覆盖。' }
        $known=@(@($slotInfo.Files | ForEach-Object { [string]$_ })+@('slot-files.json'))
        foreach ($item in @(Get-ChildItem -LiteralPath $slot -Recurse -Force)) {
            Assert-NoLink $item.FullName
            if (-not $item.PSIsContainer -and $known -notcontains $item.FullName.Substring($slot.Length+1)) { throw '已有更新助手版本槽包含未登记文件，未覆盖。' }
        }
    }
    $active=Join-Path $updateHost 'active.txt'; $pendingActive=Join-Path $updateHost 'active.pending'
    Assert-NoLink $active; Assert-NoLink $pendingActive
    if ($null -ne $old) {
        Replace-ManualFile $active (Join-Path $transaction 'active-after') $manualJournal.ActiveBeforeHash ([string]$manualJournal.ActiveAfterHash)
        Restore-ManualService ([pscustomobject]$manualJournal)
        [IO.File]::Move((Join-Path $transaction 'commit-ready'),(Join-Path $transaction 'committed'))
        $manualCommitted=$true
        try { Finish-ManualTransaction $transaction ([pscustomobject]$manualJournal); $manualPrepared=$false }
        catch { $warnings+=@('安装已完成，但旧文件备份未能完全清理；下次安装时会再次检查。') }
    } else {
        if (Test-Path -LiteralPath $pendingActive) { throw '更新助手存在未确认归属的临时记录，未覆盖。' }
        [IO.File]::WriteAllText($pendingActive,$version,[Text.Encoding]::ASCII)
        if (Test-Path -LiteralPath $active) { [IO.File]::Replace($pendingActive,$active,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($pendingActive,$active) }
    }
    if ($null -eq $old) {
        New-Item -Path 'HKLM:\SOFTWARE\WattMonitor' -Force | Out-Null
        New-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WattMonitor' -Name InstallDirectory -Value $target -PropertyType String -Force | Out-Null
        $binPath = '"'+$serviceExe+'" --owner-sid '+$sid.Value
        $result = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{ Name='WattMonitor'; DisplayName='WattHalo 硬件采集'; PathName=$binPath; StartMode='Automatic'; StartName='LocalSystem'; ServiceType=[byte]16; ErrorControl=[byte]1; DesktopInteract=$false }
        if ($result.ReturnValue -ne 0) { throw "采集服务配置失败，Windows代码：$($result.ReturnValue)" }
        Start-Service -Name WattMonitor
        $controller=Get-Service -Name WattMonitor
        try { $controller.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)) }
        finally { $controller.Dispose() }
    }
    $appExe=Join-Path $target 'WattMonitor.App.exe'
    $shortcutPath=Join-Path $DesktopDirectory 'WattHalo.lnk'
    $shell=New-Object -ComObject WScript.Shell
    $shortcutReady=$false
    try {
        $existing=Get-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
        if ($null -ne $existing -and -not (Test-OwnShortcut $shell $shortcutPath $appExe)) {
            $warnings+=@('桌面已有同名其他快捷方式，未覆盖；可从安装目录启动。')
        } else {
            $shortcut=$shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath=$appExe; $shortcut.WorkingDirectory=$target
            $shortcut.Description='WattHalo 本机能耗监测'; $shortcut.IconLocation=$appExe+',0'; $shortcut.Save()
            if (-not (Test-OwnShortcut $shell $shortcutPath $appExe)) { throw '保存后无法确认快捷方式目标。' }
            $shortcutReady=$true
        }
    } catch { $warnings+=@('桌面快捷方式未更新；原有快捷方式已保留。') }
    if ($shortcutReady) {
        foreach ($desktop in $desktopDirectories) {
            foreach ($name in @('能耗监测.lnk','WattMonitor.lnk','WattHalo.lnk')) {
                $oldShortcut=Join-Path $desktop $name
                if ([string]::Equals($oldShortcut,$shortcutPath,[StringComparison]::OrdinalIgnoreCase)) { continue }
                if (Test-OwnShortcut $shell $oldShortcut $appExe) {
                    try { Remove-Item -LiteralPath $oldShortcut } catch { $warnings+=@('旧快捷方式未能移除，已保留。') }
                }
            }
        }
    }
    $uninstallKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WattMonitor'
    try {
        if ((Test-Path -LiteralPath $uninstallKey) -and -not (Test-OwnUninstallEntry $uninstallKey $target)) {
            $warnings+=@('系统中已有同名卸载登记，未覆盖；可使用安装目录中的 Uninstall.cmd 卸载。')
        } else {
            if (-not (Test-Path -LiteralPath $uninstallKey)) { New-Item -Path $uninstallKey -ErrorAction Stop | Out-Null }
            $uninstallCommand='"'+(Join-Path $env:SystemRoot 'System32\cmd.exe')+'" /d /s /c ""'+(Join-Path $target 'Uninstall.cmd')+'""'
            $values=[ordered]@{ WattMonitorProduct='WattMonitor'; InstallLocation=$target; DisplayName='WattHalo'; DisplayVersion=$version;
                Publisher='WattMonitor contributors'; DisplayIcon='"'+$appExe+'",0'; UninstallString=$uninstallCommand }
            foreach ($entry in $values.GetEnumerator()) { New-ItemProperty -LiteralPath $uninstallKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null }
            foreach ($name in @('NoModify','NoRepair')) { New-ItemProperty -LiteralPath $uninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
        }
    } catch { $warnings+=@('未能更新系统卸载登记；可使用安装目录中的 Uninstall.cmd 卸载。') }
    $message="安装完成。请双击桌面「WattHalo」启动；原有开机自启设置会保留。`n`n安装日志：$log"
    if ($warnings.Count) { $message+="`n`n"+($warnings -join "`n"); foreach ($warning in $warnings) { Write-Warning $warning } }
    $pawn = @(Get-CimInstance -ClassName Win32_SystemDriver -Filter "Name LIKE 'PawnIO%'" -ErrorAction SilentlyContinue | Where-Object State -eq 'Running')
    if (-not $pawn.Count) { $message += "`n`n未发现运行中的 PawnIO 驱动。CPU功率可能暂不可用，请按README从官方安装后重启：`nhttps://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0`n安装器不会重复安装或自动执行驱动下载。" }
    if ($Quiet) { Write-Output $message }
    else { [Windows.Forms.MessageBox]::Show($message,'WattHalo 安装完成','OK','Information') | Out-Null }
} catch {
    $failure = $_
    $message = $failure.Exception.Message
    if ($manualPrepared -and -not $manualCommitted -and $null -ne $manualTransaction -and (Test-Path -LiteralPath $manualTransaction)) {
        try {
            $diskJournal=Get-Content -LiteralPath (Join-Path $manualTransaction 'journal.json') -Raw | ConvertFrom-Json
            Recover-ManualTransaction $manualTransaction $diskJournal
            $message += "`n原安装文件和采集服务状态已恢复。"
        } catch { $message += "`n自动恢复未完成；原文件备份已保留，请重新运行安装器恢复。原因：$($_.Exception.Message)" }
    } elseif ($manualCommitted) { $message += "`n新版本已提交，原文件备份不会用于回滚。" }
    if ($null -ne $pendingSlot -and $null -ne $updateHost) {
        try {
            $temporary=[IO.Path]::GetFullPath($pendingSlot)
            $versionsRoot=[IO.Path]::GetFullPath((Join-Path $updateHost 'versions')).TrimEnd('\')+'\'
            if (-not [string]::Equals((Split-Path -Parent $temporary),$versionsRoot.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $temporary) -notmatch '^\.pending-[0-9a-f]{32}$') { throw '临时目录归属校验失败。' }
            Assert-NoLink $temporary
            if (Test-Path -LiteralPath $temporary -PathType Container) {
                $slotDirectories=@()
                foreach ($relative in @($updaterFiles | ForEach-Object { ([string]$_.Path).Substring(8) })+@('slot-files.json')) {
                    $file=Resolve-PayloadPath $temporary $relative
                    if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
                    $directory=Split-Path -Parent $file
                    while ($directory -and -not [string]::Equals($directory,$temporary,[StringComparison]::OrdinalIgnoreCase)) { $slotDirectories+=@($directory); $directory=Split-Path -Parent $directory }
                }
                foreach ($directory in @($slotDirectories | Sort-Object -Unique | Sort-Object Length -Descending)) {
                    Assert-NoLink $directory
                    if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory }
                }
                if (@(Get-ChildItem -LiteralPath $temporary -Force).Count -eq 0) { Remove-Item -LiteralPath $temporary }
                else { throw '临时目录包含未登记文件。' }
            }
        } catch { $message += "`n无法清理本次助手临时目录：$pendingSlot" }
    }
    if ($stoppedExistingService -and -not $copyStarted -and -not $manualPrepared) {
        try { Start-Service -Name WattMonitor -ErrorAction Stop; $message += "`n原有采集服务已恢复。" }
        catch { $message += "`n原有采集服务未能恢复，请修复安装后重试。" }
    }
    if (-not $RepairService) {
        try {
            $earlyLog=Join-Path $PSScriptRoot ('installation-error-'+[guid]::NewGuid().ToString('N')+'.log')
            Assert-NoLink $earlyLog
            @($failure.Exception.ToString(),$failure.ScriptStackTrace) | Set-Content -LiteralPath $earlyLog -Encoding UTF8
            $message += "`n诊断日志：$earlyLog"
        } catch { }
    }
    Write-Error $message -ErrorAction Continue
    if ($transcribing) { $message += "`n`n日志：$log" }
    if (-not $Quiet) { try { Add-Type -AssemblyName System.Windows.Forms; $title=if($RepairService){'WattHalo 采集服务修复未完成'}else{'WattHalo 安装未完成'}; [Windows.Forms.MessageBox]::Show($message,$title,'OK','Error') | Out-Null } catch { } }
    exit 1
} finally {
    if ($null -ne $updateLock) { $updateLock.Dispose() }
    if ($transcribing) { Stop-Transcript | Out-Null }
}
