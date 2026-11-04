[CmdletBinding()]
param([string]$SigningKeyPath = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WattHalo\release-signing-key.pk8.dpapi'),
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$OutputName = 'release', [switch]$Unsigned)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Unsigned -and -not $PSBoundParameters.ContainsKey('OutputName')) { $OutputName = 'local' }
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path (Join-Path $repo 'artifacts') $OutputName
$payload = Join-Path $release 'app'
$zipName = if ($Unsigned) { 'WattHalo-local-win-x64.zip' } else { 'WattHalo-win-x64.zip' }
$zip = Join-Path $release $zipName
$updateZip = Join-Path $release 'WattHalo-update.zip'
function Assert-NoLink([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "发布路径包含链接：$current" }
        $parent=[IO.Path]::GetDirectoryName($current.TrimEnd('\')); if ($parent -eq $current) { break }; $current=$parent
    }
}
Assert-NoLink $release
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'update-public.pem') -PathType Leaf)) { throw '缺少固定发布公钥，不能构建发行包。' }
if (-not $Unsigned) {
    if (-not (Test-Path -LiteralPath $SigningKeyPath -PathType Leaf)) { throw '找不到外部 DPAPI 发布签名密钥，不能构建可更新发行包。' }
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) { throw '签名需要 PowerShell 7（pwsh）。' }
}
foreach ($document in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $document) -PathType Leaf)) { throw "缺少发布文档：$document；请先准备根目录文档。" }
}
if (Test-Path -LiteralPath $release) {
    $resolved = (Resolve-Path -LiteralPath $release).Path
    if ($resolved -ne [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'artifacts') $OutputName))) { throw '发布目录校验失败。' }
    $items = @(Get-ChildItem -LiteralPath $release -Recurse)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw '发布目录存在链接，请手动检查。' }
    foreach ($item in $items | Where-Object { -not $_.PSIsContainer }) { Remove-Item -LiteralPath $item.FullName }
    foreach ($item in $items | Where-Object { $_.PSIsContainer } | Sort-Object { $_.FullName.Length } -Descending) { Remove-Item -LiteralPath $item.FullName }
} else { New-Item -ItemType Directory -Path $release | Out-Null }
New-Item -ItemType Directory -Path $payload | Out-Null
& dotnet publish (Join-Path $repo 'src\WattMonitor.App\WattMonitor.App.csproj') -c Release -r win-x64 --self-contained true -o $payload --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '桌面应用发布失败。' }
& dotnet publish (Join-Path $repo 'src\WattMonitor.Service\WattMonitor.Service.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $payload 'service') --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '采集服务发布失败。' }
& dotnet publish (Join-Path $repo 'src\WattMonitor.Updater\WattMonitor.Updater.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $payload 'updater') --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '更新助手发布失败。' }
foreach ($document in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repo $document) -Destination (Join-Path $payload $document) }
$docs=Join-Path $repo 'docs'
if (Test-Path -LiteralPath $docs -PathType Container) {
    foreach ($document in Get-ChildItem -LiteralPath $docs -Recurse -File -Filter '*.md') {
        Assert-NoLink $document.FullName
        $out=Join-Path (Join-Path $payload 'docs') $document.FullName.Substring($docs.Length+1)
        New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null
        Copy-Item -LiteralPath $document.FullName -Destination $out
    }
}
foreach ($script in @('Install.cmd','Uninstall.cmd','Install.ps1','Uninstall.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $payload $script) }
$licenseRoot=Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenseRoot | Out-Null
$dependencies=@{}
foreach ($project in @('WattMonitor.App','WattMonitor.Service')) {
    $assets=Get-Content -LiteralPath (Join-Path $repo "src\$project\obj\project.assets.json") -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $dependencies.ContainsKey($library.Name)) { continue }
        $package=$null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate=Join-Path $folder $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package=$candidate; break }
        }
        if ($null -eq $package) { throw "找不到依赖缓存：$($library.Name)" }
        $nuspec=@(Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File)[0]
        [xml]$xml=Get-Content -LiteralPath $nuspec.FullName -Encoding UTF8 -Raw
        $metadata=$xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $licenseNode=$metadata.SelectSingleNode('*[local-name()="license"]')
        $urlNode=$metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $repositoryNode=$metadata.SelectSingleNode('*[local-name()="repository"]')
        $destination=Join-Path $licenseRoot ($library.Name.Replace('/','.'))
        New-Item -ItemType Directory -Path $destination | Out-Null
        $notices=@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Name -match '^(?i:LICENSE|LICENCE|COPYING|THIRD.?PARTY|NOTICE)' -or $_.Extension -eq '.nuspec' })
        if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
            $declared=[IO.Path]::GetFullPath((Join-Path $package $licenseNode.InnerText))
            if (-not $declared.StartsWith($package.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '依赖许可路径越界。' }
            $notices+=Get-Item -LiteralPath $declared
        }
        foreach ($notice in @($notices | Sort-Object FullName -Unique)) {
            $relative=$notice.FullName.Substring($package.Length+1)
            $out=Join-Path $destination $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $out
        }
        $dependencies[$library.Name]=[ordered]@{
            Package=$library.Name; License=$(if($null -ne $licenseNode){$licenseNode.InnerText}else{$null});
            LicenseType=$(if($null -ne $licenseNode){$licenseNode.GetAttribute('type')}else{$null});
            LicenseUrl=$(if($null -ne $urlNode){$urlNode.InnerText}else{$null});
            Repository=$(if($null -ne $repositoryNode){$repositoryNode.GetAttribute('url')}else{$null});
            RepositoryCommit=$(if($null -ne $repositoryNode){$repositoryNode.GetAttribute('commit')}else{$null});
            NoticeDirectory='licenses/'+($library.Name.Replace('/','.'))
        }
    }
}
# The NuGet binary omits these upstream notices, including LGPL-2.1 PawnIO modules embedded in LHM.
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$lhmCommit='3d331e3370efb858411f19511373eff65a218701'
$lhmNotices=Join-Path $licenseRoot 'LibreHardwareMonitorLib.0.9.6\upstream'
foreach ($relative in @('LICENSE','THIRD-PARTY-NOTICES.txt','LibreHardwareMonitorLib/Resources/PawnIo/COPYING','LibreHardwareMonitorLib/Resources/PawnIo/README')) {
    $out=Join-Path $lhmNotices $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing -Uri ("https://raw.githubusercontent.com/LibreHardwareMonitor/LibreHardwareMonitor/$lhmCommit/"+$relative) -OutFile $out
}
@(
    "LibreHardwareMonitor 0.9.6 source (unmodified dependency): https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/$lhmCommit",
    "Source archive: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/archive/$lhmCommit.zip",
    'Embedded signed PawnIO.Modules source and binaries, release 0.1.6 (LGPL-2.1): https://github.com/namazso/PawnIO.Modules/releases/tag/0.1.6',
    'PawnIO.Modules source: https://github.com/namazso/PawnIO.Modules/tree/0.1.6',
    'The shared PawnIO kernel driver is not bundled or installed by this package.'
) | Set-Content -LiteralPath (Join-Path $lhmNotices 'SOURCE-LINKS.txt') -Encoding UTF8
@($dependencies.Keys | Sort-Object | ForEach-Object { $dependencies[$_] }) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $licenseRoot 'dependencies.json') -Encoding UTF8
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    if ($_.Extension -eq '.log') { throw '发布目录出现运行日志，已停止打包以保护个人数据。' }
    [ordered]@{ Path = $_.FullName.Substring($payload.Length + 1); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$fileVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'WattMonitor.App.exe'))
$version='{0}.{1}.{2}' -f $fileVersion.FileMajorPart,$fileVersion.FileMinorPart,$fileVersion.FileBuildPart
if ($version -eq '0.0.0') { throw '应用版本信息缺失，已停止打包。' }
[ordered]@{ Product='WattMonitor'; Format=1; Version=$version; Files=$files } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'payload-manifest.json') -Encoding UTF8
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash + '  ' + $zipName | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
if (-not $Unsigned) {
    Copy-Item -LiteralPath $zip -Destination $updateZip
    $manifestFile = Join-Path $release 'WattHalo-update.json'
    $signatureFile = Join-Path $release 'WattHalo-update.sig'
    $updateManifest = [ordered]@{ Product='WattMonitor'; Format=1; Version=$version; Archive='WattHalo-update.zip'; Size=(Get-Item -LiteralPath $updateZip).Length; Sha256=(Get-FileHash -LiteralPath $updateZip -Algorithm SHA256).Hash } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($manifestFile, $updateManifest, (New-Object Text.UTF8Encoding($false)))
    & $pwsh.Source -NoProfile -File (Join-Path $PSScriptRoot 'Sign-Update.ps1') -KeyPath $SigningKeyPath -ManifestPath $manifestFile -SignaturePath $signatureFile
    if ($LASTEXITCODE -ne 0) { throw '更新清单签名失败，不能发布。' }
} else { Write-Warning '这是未签名的本地构建，不包含官方更新资产。' }
Write-Host "发布完成：$zip"
