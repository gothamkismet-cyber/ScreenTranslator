$ErrorActionPreference = 'Stop'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorArchive = Join-Path $translatorRoot '.tools\downloads\dotnet-sdk-10.0.401-win-x64-blob.zip'
$translatorSdk = Join-Path $translatorRoot '.tools\dotnet'
$translatorHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
if (Test-Path -LiteralPath (Join-Path $translatorSdk 'sdk\10.0.401\Sdks\Microsoft.NET.Sdk\Sdk\Sdk.props')) { Write-Output '项目 SDK 已准备。'; exit 0 }
New-Item -ItemType Directory -Path (Split-Path -Parent $translatorArchive), $translatorSdk -Force | Out-Null
if (-not (Test-Path -LiteralPath $translatorArchive) -or (Get-FileHash -LiteralPath $translatorArchive -Algorithm SHA512).Hash -ne $translatorHash) {
    & curl.exe --fail --location --silent --show-error --retry 2 --output $translatorArchive 'https://dotnetcli.blob.core.windows.net/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip'
    if ($LASTEXITCODE -ne 0) { throw '项目 SDK 下载失败。' }
}
if ((Get-FileHash -LiteralPath $translatorArchive -Algorithm SHA512).Hash -ne $translatorHash) { throw '项目 SDK 校验失败，未执行该文件。' }
Expand-Archive -LiteralPath $translatorArchive -DestinationPath $translatorSdk -Force
& (Join-Path $translatorSdk 'dotnet.exe') --version
