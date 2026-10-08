$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$translatorRoot = Split-Path -Parent $PSScriptRoot
$translatorModelRoot = Join-Path $translatorRoot 'models\screen-ocr'
New-Item -ItemType Directory -Path $translatorModelRoot -Force | Out-Null
$translatorModelBase = 'https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5'
$translatorModels = @(
    @{ Name='det.onnx'; Path='det/ch_PP-OCRv5_det_mobile.onnx'; Sha256='4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae' },
    @{ Name='cls.onnx'; Path='cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'; Sha256='54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7' },
    @{ Name='en-rec.onnx'; Path='rec/en_PP-OCRv5_rec_mobile.onnx'; Sha256='c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8' },
    @{ Name='ja-rec.onnx'; Path='rec/ch_PP-OCRv5_rec_mobile.onnx'; Sha256='5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5' },
    @{ Name='ko-rec.onnx'; Path='rec/korean_PP-OCRv5_rec_mobile.onnx'; Sha256='cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b' }
)
$translatorReceipt = @()
foreach ($translatorModel in $translatorModels) {
    $translatorTarget = Join-Path $translatorModelRoot $translatorModel.Name
    $translatorUrl = "$translatorModelBase/$($translatorModel.Path)"
    if (-not (Test-Path -LiteralPath $translatorTarget)) { Invoke-WebRequest -Uri $translatorUrl -OutFile $translatorTarget }
    $translatorActualHash = (Get-FileHash -LiteralPath $translatorTarget -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($translatorActualHash -ne $translatorModel.Sha256) { throw "Model hash mismatch: $($translatorModel.Name)" }
    $translatorReceipt += [PSCustomObject]@{ Name=$translatorModel.Name; Source=$translatorUrl; Sha256=$translatorActualHash; Bytes=(Get-Item -LiteralPath $translatorTarget).Length; CharacterDictionary='Extracted from this ONNX file character metadata'; License='Apache-2.0; see THIRD_PARTY_NOTICES.md' }
    Write-Output "Verified model: $($translatorModel.Name)"
}
$translatorReceipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $translatorModelRoot 'manifest.json') -Encoding utf8
