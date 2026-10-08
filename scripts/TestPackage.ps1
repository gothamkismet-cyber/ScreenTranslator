#requires -Version 7.2
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$translatorRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本需要三段数字。' }
$translatorRelease = Join-Path $translatorRoot 'artifacts\releases'
$translatorSetup = Join-Path $translatorRelease ('ScreenTranslator-' + $Version + '-win-x64-Setup.exe')
$translatorZip = Join-Path $translatorRelease ('ScreenTranslator-' + $Version + '-win-x64-Portable.zip')
$translatorTestRoot = Join-Path $translatorRoot ('artifacts\verification\packages\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$translatorInstallRoot = [IO.Path]::GetFullPath((Join-Path $translatorTestRoot 'installed'))
if (-not $translatorInstallRoot.StartsWith($translatorRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '安装测试目录必须在项目内。' }
$translatorUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{DB573468-89A6-4D5F-B4D3-64284DBD1E32}_is1'
if (Test-Path -LiteralPath $translatorUninstallKey) { throw '已存在本软件的安装记录。为保留现有安装，本脚本不覆盖它；请在干净测试账户运行。' }
New-Item -ItemType Directory -Force -Path $translatorTestRoot | Out-Null
$translatorRows = [Collections.Generic.List[object]]::new()
$translatorPassed = $false
$translatorDotnetVariables = @{}

function Test-PackageFiles([string]$Directory) {
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne $Version -or -not $manifest.selfContained) { throw '安装内容版本或自包含声明不符。' }
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $Directory $entry.path))
        if (-not $path.StartsWith([IO.Path]::GetFullPath($Directory) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '文件清单越过安装目录。' }
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw ('文件丢失或校验不符：' + $entry.path) }
    }
    $translatorRows.Add([pscustomobject]@{check='all packaged files match manifest';directory=[IO.Path]::GetFileName($Directory);files=$manifest.files.Count;passed=$true})
}

function Test-PackageUi([string]$Executable,[string]$Output) {
    # --verify-ui uses synthetic text and its own SettingsStore, never the user's actual keys.
    $process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -ArgumentList @('--verify-ui',('"' + $Output + '"')) -WindowStyle Hidden -PassThru
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        while (-not $process.WaitForExit(1000)) { if ($timer.Elapsed.TotalSeconds -ge 60) { throw '发行程序界面测试超时。' } }
        $reportPath = Join-Path $Output 'report.json'
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) { throw '发行程序界面测试失败。' }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if (-not $report.passed) { throw '发行程序界面或三语模型加载检查未通过。' }
        $translatorRows.Add([pscustomobject]@{check='actual distributed exe initializes UI, saves isolated settings, and loads all three OCR models without DOTNET_ROOT';directory=[IO.Path]::GetFileName((Split-Path -Parent $Executable));passed=$true;rows=$report.rows.Count})
    }
    finally { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(1000) | Out-Null }; $process.Dispose() }
}

try {
    foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64')) {
        $translatorDotnetVariables[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$null,'Process')
    }
    foreach ($line in (Get-Content -LiteralPath (Join-Path $translatorRelease 'SHA256SUMS.txt'))) {
        if ($line -notmatch '^([0-9a-f]{64})  (ScreenTranslator-[A-Za-z0-9_.-]+)$') { throw '校验文件格式不符。' }
        if ((Get-FileHash -LiteralPath (Join-Path $translatorRelease $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Matches[1]) { throw '发行附件 SHA256 校验失败。' }
    }
    $translatorRows.Add([pscustomobject]@{check='release asset SHA256 checksums';passed=$true})
    $translatorPortableRoot = Join-Path $translatorTestRoot 'portable'
    Expand-Archive -LiteralPath $translatorZip -DestinationPath $translatorPortableRoot
    $translatorPortableApp = Join-Path $translatorPortableRoot 'ScreenTranslator'
    Test-PackageFiles $translatorPortableApp
    Test-PackageUi (Join-Path $translatorPortableApp 'ScreenTranslator.exe') (Join-Path $translatorTestRoot 'portable-ui')
    $translatorInstallArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $translatorInstallRoot + '"'),('/LOG="' + (Join-Path $translatorTestRoot 'install.log') + '"'))
    $translatorInstallProcess = Start-Process -FilePath $translatorSetup -ArgumentList $translatorInstallArguments -WindowStyle Hidden -PassThru -Wait
    if ($translatorInstallProcess.ExitCode -ne 0) { throw ('安装失败：' + $translatorInstallProcess.ExitCode) }
    if (-not (Test-Path -LiteralPath $translatorUninstallKey)) { throw '缺少当前用户的卸载记录。' }
    $translatorRegistration = Get-ItemProperty -LiteralPath $translatorUninstallKey
    if ([IO.Path]::GetFullPath($translatorRegistration.InstallLocation).TrimEnd('\') -ne $translatorInstallRoot.TrimEnd('\')) { throw '卸载记录不属于本次项目内测试目录。' }
    $translatorRows.Add([pscustomobject]@{check='real current-user install and uninstall registration in project test directory';passed=$true})
    Test-PackageFiles $translatorInstallRoot
    Test-PackageUi (Join-Path $translatorInstallRoot 'ScreenTranslator.exe') (Join-Path $translatorTestRoot 'installed-ui')
    $translatorPassed = $true
}
catch { $translatorRows.Add([pscustomobject]@{check='package test exception';passed=$false;error=$_.Exception.GetType().Name;message=$_.Exception.Message}) }
finally {
    $translatorUninstaller = Join-Path $translatorInstallRoot 'unins000.exe'
    if (Test-Path -LiteralPath $translatorUninstaller) {
        # This is the installer created above, whose absolute target was validated inside the workspace.
        $translatorUninstallProcess = Start-Process -FilePath $translatorUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $translatorTestRoot 'uninstall.log') + '"')) -WindowStyle Hidden -PassThru -Wait
        $translatorUninstallOkay = $translatorUninstallProcess.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $translatorUninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $translatorInstallRoot 'ScreenTranslator.exe'))
        $translatorRows.Add([pscustomobject]@{check='real uninstall removes product and uninstall registration';passed=$translatorUninstallOkay})
        $translatorPassed = $translatorPassed -and $translatorUninstallOkay
    }
    foreach ($name in $translatorDotnetVariables.Keys) { [Environment]::SetEnvironmentVariable($name,$translatorDotnetVariables[$name],'Process') }
    [ordered]@{test='packaged installer and ZIP, isolated Windows runtime checks';utcTime=[DateTimeOffset]::UtcNow;version=$Version;passed=$translatorPassed;realAiCalls=0;normalUserSettingsTouched=$false;rows=$translatorRows} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $translatorTestRoot 'report.json') -Encoding utf8
    Write-Output ('安装包测试记录：' + (Join-Path $translatorTestRoot 'report.json'))
}
if (-not $translatorPassed) { throw '安装包检查未通过，请查看报告。' }
Write-Output '安装、文件校验、免安装启动、安装后启动和卸载均已通过。'
