param(
    [switch]$FrameworkDependent,
    [switch]$VerifyUiWindows
)
$ErrorActionPreference = 'Stop'
$reclaimRoot = $PSScriptRoot
Push-Location -LiteralPath $reclaimRoot
try {
    function Invoke-ReclaimDotnet {
        param([string[]]$ReclaimArguments)
        & dotnet @ReclaimArguments
        if ($LASTEXITCODE -ne 0) { throw "Build or verification failed (exit $LASTEXITCODE)." }
    }

    $reclaimVerification = ((Join-Path $reclaimRoot '.build/verification').Replace('\', '/') + '/')
    Invoke-ReclaimDotnet @('build', 'Reclaim.slnx', '-c', 'Release', '--disable-build-servers', '-m:1', "-p:OutDir=$reclaimVerification", '--nologo')
    Invoke-ReclaimDotnet @((Join-Path $reclaimVerification 'Reclaim.Tests.dll'), 'artifacts/tests')
    Invoke-ReclaimDotnet @((Join-Path $reclaimVerification 'Reclaim.UiTests.dll'), 'artifacts/tests/sample-ntfs.img')
    Invoke-ReclaimDotnet @((Join-Path $reclaimVerification 'Reclaim.UiTests.dll'), 'artifacts/tests/sample-ntfs.img', '--layout')
    if ($VerifyUiWindows) {
        Invoke-ReclaimDotnet @((Join-Path $reclaimVerification 'Reclaim.UiTests.dll'), '--buttons')
        Invoke-ReclaimDotnet @((Join-Path $reclaimVerification 'Reclaim.UiTests.dll'), 'artifacts/tests/sample-ntfs.img', '--browser')
    }

    $reclaimDist = [IO.Path]::GetFullPath((Join-Path $reclaimRoot 'dist'))
    $reclaimOutput = [IO.Path]::GetFullPath((Join-Path $reclaimDist 'Reclaim'))
    $reclaimExpected = [IO.Path]::GetFullPath((Join-Path $reclaimRoot 'dist/Reclaim'))
    if ($reclaimOutput -ne $reclaimExpected -or -not $reclaimOutput.StartsWith($reclaimRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The output directory must stay inside this repository under dist/Reclaim.'
    }
    foreach ($reclaimPath in @($reclaimDist, $reclaimOutput)) {
        if ((Test-Path -LiteralPath $reclaimPath) -and ((Get-Item -LiteralPath $reclaimPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The output directory must not be a junction or symbolic link.'
        }
    }
    if (Test-Path -LiteralPath $reclaimOutput) { Remove-Item -LiteralPath $reclaimOutput -Recurse -Force }
    New-Item -ItemType Directory -Path $reclaimOutput -Force | Out-Null
    $reclaimStandalone = (-not $FrameworkDependent).ToString().ToLowerInvariant()
    Invoke-ReclaimDotnet @('publish', 'src/Reclaim.App/Reclaim.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', $reclaimStandalone,
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableSingleFileAnalyzer=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $reclaimOutput, '--nologo', '--disable-build-servers', '-m:1')

    # These SDK XML files are developer documentation, not runtime dependencies.
    foreach ($reclaimDoc in @('Microsoft.Web.WebView2.Core.xml', 'Microsoft.Web.WebView2.WinForms.xml')) {
        $reclaimDocPath = Join-Path $reclaimOutput $reclaimDoc
        if (Test-Path -LiteralPath $reclaimDocPath) { Remove-Item -LiteralPath $reclaimDocPath -Force }
    }
    $reclaimLicenses = Join-Path $reclaimOutput 'Licenses'
    New-Item -ItemType Directory -Path $reclaimLicenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $reclaimRoot 'licenses/WebView2-LICENSE.txt'), (Join-Path $reclaimRoot 'licenses/WebView2-NOTICE.txt') -Destination $reclaimLicenses
    if (-not $FrameworkDependent) {
        foreach ($reclaimRuntime in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
            $reclaimPackage = Get-ChildItem -LiteralPath (Join-Path $reclaimRoot ".build/packages/$reclaimRuntime") -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
            $reclaimNotices = if ($reclaimRuntime -eq 'microsoft.netcore.app.runtime.win-x64') { @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') } else { @('LICENSE') }
            foreach ($reclaimNotice in $reclaimNotices) {
                $reclaimNoticePath = Join-Path $reclaimPackage.FullName $reclaimNotice
                if (-not (Test-Path -LiteralPath $reclaimNoticePath)) { throw "Runtime notice missing: $reclaimNoticePath" }
                $reclaimNoticeName = if ([IO.Path]::GetExtension($reclaimNotice)) { $reclaimNotice } else { "$reclaimNotice.txt" }
                Copy-Item -LiteralPath $reclaimNoticePath -Destination (Join-Path $reclaimLicenses "$reclaimRuntime-$reclaimNoticeName")
            }
        }
    }
    $reclaimGuide = [IO.File]::ReadAllText((Join-Path $reclaimRoot 'docs/USER-GUIDE.md'))
    if ($FrameworkDependent) { $reclaimGuide = $reclaimGuide.Replace('The .NET runtime is included. No separate .NET installation is required.', 'Install .NET 10 Desktop Runtime (Windows x64) before starting this build.') }
    [IO.File]::WriteAllText((Join-Path $reclaimOutput 'README.md'), $reclaimGuide, [Text.UTF8Encoding]::new($false))
    $reclaimArchive = Join-Path $reclaimDist 'Reclaim-Windows-x64.zip'
    Compress-Archive -LiteralPath $reclaimOutput -DestinationPath $reclaimArchive -Force
    Write-Output "Ready: $reclaimOutput/Reclaim.exe"
    Write-Output "Release ZIP: $reclaimArchive"
}
finally { Pop-Location }
