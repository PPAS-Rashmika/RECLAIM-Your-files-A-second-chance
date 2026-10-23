param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$Image, [Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$reclaimBefore = (Get-FileHash -LiteralPath $Image -Algorithm SHA256).Hash
& $Executable --image $Image --out $OutputDirectory --all
if ($LASTEXITCODE -ne 0) { throw 'Rescue console image recovery failed.' }
$reclaimReport = Get-ChildItem -LiteralPath $OutputDirectory -Filter rescue-report.tsv -Recurse | Sort-Object LastWriteTimeUtc | Select-Object -Last 1
if ($null -eq $reclaimReport) { throw 'Rescue report is missing.' }
$reclaimRows = @(Import-Csv -LiteralPath $reclaimReport.FullName -Delimiter "`t")
if (-not ($reclaimRows | Where-Object { $_.Format -eq 'png' -and $_.Status -eq 'saved' }) -or -not ($reclaimRows | Where-Object { $_.Format -eq 'pdf' -and $_.Status -eq 'saved' })) { throw 'Expected checksum-validated PNG and PDF exports are missing.' }
foreach ($reclaimRow in $reclaimRows | Where-Object Status -EQ 'saved') {
    $reclaimFile = Join-Path $reclaimReport.DirectoryName $reclaimRow.File
    if ((Get-Item -LiteralPath $reclaimFile).Length -ne [long]$reclaimRow.Size) { throw 'Rescue export size does not match its report.' }
    $reclaimSource = [IO.File]::OpenRead([IO.Path]::GetFullPath($Image))
    try {
        $reclaimSource.Position = [long]$reclaimRow.Offset
        $reclaimExpected = New-Object byte[] ([int]$reclaimRow.Size)
        $reclaimRead = 0
        while ($reclaimRead -lt $reclaimExpected.Length) {
            $reclaimCount = $reclaimSource.Read($reclaimExpected, $reclaimRead, $reclaimExpected.Length - $reclaimRead)
            if ($reclaimCount -eq 0) { throw 'Truncated rescue test source.' }
            $reclaimRead += $reclaimCount
        }
        $reclaimActual = [IO.File]::ReadAllBytes($reclaimFile)
        if ([Convert]::ToBase64String($reclaimExpected) -ne [Convert]::ToBase64String($reclaimActual)) { throw 'Rescue bytes do not match the source.' }
    }
    finally { $reclaimSource.Dispose() }
}
if ((Get-FileHash -LiteralPath $Image -Algorithm SHA256).Hash -ne $reclaimBefore) { throw 'Rescue changed the source image.' }
Write-Output 'PASS Independent rescue console exports exact candidate bytes and leaves its synthetic source unchanged. ISO boot was not tested.'
Add-Type -AssemblyName System.Drawing
$reclaimJpeg = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'jpeg-fixture.jpg'
$reclaimBitmap = New-Object Drawing.Bitmap 24,18
try {
    $reclaimGraphics = [Drawing.Graphics]::FromImage($reclaimBitmap)
    try { $reclaimGraphics.Clear([Drawing.Color]::CornflowerBlue) }
    finally { $reclaimGraphics.Dispose() }
    $reclaimBitmap.Save($reclaimJpeg, [Drawing.Imaging.ImageFormat]::Jpeg)
}
finally { $reclaimBitmap.Dispose() }
$reclaimJpegBytes = [IO.File]::ReadAllBytes($reclaimJpeg)
$reclaimJpegImage = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'jpeg-fixture.img'
$reclaimJpegRaw = New-Object byte[] ($reclaimJpegBytes.Length + 4096)
[Array]::Copy($reclaimJpegBytes, 0, $reclaimJpegRaw, 1024, $reclaimJpegBytes.Length)
[IO.File]::WriteAllBytes($reclaimJpegImage, $reclaimJpegRaw)
$reclaimJpegOutput = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'jpeg-output'
& $Executable --image $reclaimJpegImage --out $reclaimJpegOutput --all
if ($LASTEXITCODE -ne 0) { throw 'Rescue JPEG recovery failed.' }
$reclaimRecoveredJpeg = Get-ChildItem -LiteralPath $reclaimJpegOutput -Filter '*.jpg' -Recurse | Select-Object -Last 1
if ($null -eq $reclaimRecoveredJpeg -or (Get-FileHash -LiteralPath $reclaimRecoveredJpeg.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $reclaimJpeg -Algorithm SHA256).Hash) { throw 'Rescue JPEG bytes differ from the generated photograph.' }
Write-Output 'PASS Rescue JPEG parser retains an actual encoded photograph byte for byte.'
