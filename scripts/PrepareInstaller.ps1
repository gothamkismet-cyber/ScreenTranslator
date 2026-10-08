$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorInstallerRoot = Join-Path $translatorRoot '.tools\inno-7.1.0'
$translatorInstallerDownload = Join-Path $translatorRoot '.tools\downloads\innosetup-7.1.0-x64.exe'
$translatorInstallerHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$translatorCompiler = Join-Path $translatorInstallerRoot 'ISCC.exe'
if (Test-Path -LiteralPath $translatorCompiler) {
    $translatorCompilerVersion = & $translatorCompiler --version
    if ($LASTEXITCODE -ne 0 -or $translatorCompilerVersion -ne '7.1.0') { throw '项目内安装包编译器版本不匹配。' }
    Write-Output '项目内 Inno Setup 7.1.0 已准备。'
    return
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $translatorInstallerDownload) | Out-Null
if (-not (Test-Path -LiteralPath $translatorInstallerDownload)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $translatorInstallerDownload
}
if ((Get-FileHash -LiteralPath $translatorInstallerDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $translatorInstallerHash) { throw '安装包编译工具 SHA256 校验失败，未执行。' }
$translatorSignature = Get-AuthenticodeSignature -LiteralPath $translatorInstallerDownload
if ($translatorSignature.Status -ne 'Valid' -or $translatorSignature.SignerCertificate.Subject -notmatch 'Pyrsys B.V.') { throw '安装包编译工具数字签名不符，未执行。' }
# The official portable mode avoids system installation, shortcuts, associations, and an uninstall registration.
$translatorArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/CURRENTUSER','/PORTABLE=1',('/DIR="' + $translatorInstallerRoot + '"'))
$translatorProcess = Start-Process -FilePath $translatorInstallerDownload -ArgumentList $translatorArguments -WindowStyle Hidden -PassThru -Wait
if ($translatorProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $translatorCompiler)) { throw '项目内安装包编译工具准备失败。' }
if ((& $translatorCompiler --version) -ne '7.1.0') { throw '安装包编译工具版本不符。' }
