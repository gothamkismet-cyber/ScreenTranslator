param([ValidateSet('quick','ocr','protocol','session','desktop','exclusion','main-flow','window-flow','ui','soak')][string]$Mode = 'quick')
$ErrorActionPreference = 'Stop'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorDotnet = Join-Path $translatorRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $translatorDotnet)) { throw '请先准备项目 SDK。' }
$env:DOTNET_ROOT = Split-Path -Parent $translatorDotnet
$env:DOTNET_CLI_HOME = Join-Path $translatorRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $translatorRoot '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Mode -eq 'ui') {
    & (Join-Path $PSScriptRoot 'Build.ps1')
    $translatorExe = Join-Path $translatorRoot 'src\ScreenTranslator.App\bin\Release\net10.0-windows\win-x64\ScreenTranslator.exe'
    $translatorOutput = Join-Path $translatorRoot 'artifacts\verification\ui'
    $translatorUiProcess = Start-Process -FilePath $translatorExe -ArgumentList @('--verify-ui', ('"' + $translatorOutput + '"')) -WindowStyle Hidden -PassThru
    $translatorUiProcess.WaitForExit()
    if ($translatorUiProcess.ExitCode -ne 0) { throw '界面验证失败，请查看 artifacts/verification/ui。' }
    exit 0
}
$translatorTestOutput = Join-Path $translatorRoot '.tools\test-build'
& $translatorDotnet build (Join-Path $translatorRoot 'tests\ScreenTranslator.Tests\ScreenTranslator.Tests.csproj') -c Release -o $translatorTestOutput --nologo -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw '测试程序构建失败。' }
$translatorModes = if ($Mode -eq 'quick') { @('ocr','protocol') } else { @($Mode) }
foreach ($translatorMode in $translatorModes) {
    $translatorTestArgs = @((Join-Path $translatorTestOutput 'ScreenTranslator.Tests.dll'), ('--' + $translatorMode), $translatorRoot)
    if ($translatorMode -eq 'soak') { $translatorTestArgs += '1200' }
    & $translatorDotnet @translatorTestArgs
    if ($LASTEXITCODE -ne 0) { throw ('验证失败：' + $translatorMode + '；请查看 artifacts/verification。') }
}
