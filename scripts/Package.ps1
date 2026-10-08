#requires -Version 7.2
param([string]$RepositoryUrl = 'https://github.com/gothamkismet-cyber/ScreenTranslator')
$ErrorActionPreference = 'Stop'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorProject = Join-Path $translatorRoot 'src\ScreenTranslator.App\ScreenTranslator.App.csproj'
[xml]$translatorProjectXml = Get-Content -LiteralPath $translatorProject -Raw
$translatorVersion = [string]$translatorProjectXml.Project.PropertyGroup.Version
if ($translatorVersion -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本需要三段数字。' }
if ($RepositoryUrl -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw '仓库地址必须是标准 GitHub HTTPS 地址。' }
& (Join-Path $PSScriptRoot 'PrepareInstaller.ps1')
$translatorDotnet = Join-Path $translatorRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $translatorDotnet)) { throw '请先运行 PrepareSdk.ps1。' }
& (Join-Path $PSScriptRoot 'PrepareModels.ps1')
$env:DOTNET_ROOT = Split-Path -Parent $translatorDotnet
$env:DOTNET_CLI_HOME = Join-Path $translatorRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $translatorRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$translatorStage = Join-Path $translatorRoot ('artifacts\release-stage\' + $translatorVersion + '-' + [Guid]::NewGuid().ToString('N'))
$translatorApp = Join-Path $translatorStage 'ScreenTranslator'
$translatorRelease = Join-Path $translatorRoot 'artifacts\releases'
New-Item -ItemType Directory -Force -Path $translatorApp,$translatorRelease | Out-Null
& $translatorDotnet publish $translatorProject -c Release -r win-x64 --self-contained true -o $translatorApp --nologo -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw '发布构建失败，未生成安装包。' }
foreach ($translatorDocument in @('README.md','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $translatorRoot $translatorDocument) -Destination $translatorApp }
Copy-Item -LiteralPath (Join-Path $translatorRoot 'licenses') -Destination $translatorApp -Recurse
Copy-Item -LiteralPath (Join-Path $translatorRoot 'docs') -Destination $translatorApp -Recurse
$translatorModels = Get-Content -LiteralPath (Join-Path $translatorApp 'models\screen-ocr\manifest.json') -Raw | ConvertFrom-Json
foreach ($translatorModel in $translatorModels) {
    $translatorModelPath = Join-Path $translatorApp ('models\screen-ocr\' + $translatorModel.Name)
    if ((Get-FileHash -LiteralPath $translatorModelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $translatorModel.Sha256) { throw ('打包模型校验失败：' + $translatorModel.Name) }
}
$translatorFiles = @(Get-ChildItem -LiteralPath $translatorApp -Recurse -File | Sort-Object FullName)
foreach ($translatorFile in $translatorFiles) {
    if ($translatorFile.Name -match '^(settings\.json|credentials\.dat|diagnostics\.jsonl|\.env)' -or $translatorFile.Extension -in @('.pdb','.user','.suo','.tmp')) { throw ('发行目录含有不应发布的文件：' + $translatorFile.Name) }
}
$translatorSourcePaths = @('src','tests','scripts','packaging')
$translatorSourceHashes = @(
    foreach ($translatorSourcePath in $translatorSourcePaths) {
        Get-ChildItem -LiteralPath (Join-Path $translatorRoot $translatorSourcePath) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | Sort-Object FullName | ForEach-Object {
                $translatorSourceHash = if ($_.Extension -eq '.ico') { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } else {
                    $translatorSourceText = [IO.File]::ReadAllText($_.FullName).Replace("`r`n","`n")
                    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($translatorSourceText))).ToLowerInvariant()
                }
                [pscustomobject]@{path=[IO.Path]::GetRelativePath($translatorRoot,$_.FullName).Replace('\','/');sha256=$translatorSourceHash}
            }
    }
)
$translatorManifest = [ordered]@{
    schema='screen-ai-translator/package/v1';version=$translatorVersion;platform='win-x64';repository=$RepositoryUrl
    selfContained=$true;codeSigned=$false;realAiVerification='pending';sourceHashEncoding='UTF-8 without BOM, CRLF normalized to LF; ICO uses raw bytes';sourceFiles=$translatorSourceHashes
    files=@($translatorFiles | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($translatorApp,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
}
$translatorManifestName = 'ScreenTranslator-' + $translatorVersion + '-win-x64-manifest.json'
$translatorManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $translatorApp 'package-manifest.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $translatorApp 'package-manifest.json') -Destination (Join-Path $translatorRelease $translatorManifestName)
$translatorCompiler = Join-Path $translatorRoot '.tools\inno-7.1.0\ISCC.exe'
& $translatorCompiler --quiet ('--define=ProjectRoot=' + $translatorRoot) ('--define=AppDir=' + $translatorApp) ('--define=AppVersion=' + $translatorVersion) ('--define=RepositoryUrl=' + $RepositoryUrl) ('--output-dir=' + $translatorRelease) (Join-Path $translatorRoot 'packaging\ScreenTranslator.iss')
if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
$translatorZip = Join-Path $translatorRelease ('ScreenTranslator-' + $translatorVersion + '-win-x64-Portable.zip')
Compress-Archive -LiteralPath $translatorApp -DestinationPath $translatorZip -CompressionLevel Optimal -Force
$translatorAssets = @(
    Join-Path $translatorRelease ('ScreenTranslator-' + $translatorVersion + '-win-x64-Setup.exe')
    $translatorZip
    Join-Path $translatorRelease $translatorManifestName
)
$translatorChecksums = @($translatorAssets | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) })
$translatorChecksums | Set-Content -LiteralPath (Join-Path $translatorRelease 'SHA256SUMS.txt') -Encoding ascii
$translatorAssets | Get-Item | Select-Object Name,Length
Write-Output ('已生成安装版、免安装 ZIP 与 SHA256 校验文件：' + $translatorRelease)
