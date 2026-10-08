param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorDotnet = Join-Path $translatorRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $translatorDotnet)) { throw '缺少项目内 SDK。请先准备 .tools\dotnet。' }
$env:DOTNET_ROOT = Split-Path -Parent $translatorDotnet
$env:DOTNET_CLI_HOME = Join-Path $translatorRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $translatorRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$translatorProject = Join-Path $translatorRoot 'src\ScreenTranslator.App\ScreenTranslator.App.csproj'
& $translatorDotnet build $translatorProject -c Release --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
if ($Publish) {
    & $translatorDotnet publish $translatorProject -c Release -r win-x64 --self-contained true -o (Join-Path $translatorRoot 'artifacts\app') --nologo -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw '输出测试版失败。' }
}
