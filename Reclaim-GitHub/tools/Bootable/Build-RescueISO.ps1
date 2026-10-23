param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$reclaimAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $reclaimAdmin) { throw 'Run this ISO builder from PowerShell as administrator.' }
$reclaimAdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/Assessment and Deployment Kit'
$reclaimEnvironment = Join-Path $reclaimAdk 'Deployment Tools/DandISetEnv.bat'
$reclaimPe = Join-Path $reclaimAdk 'Windows Preinstallation Environment'
$reclaimCopype = Join-Path $reclaimPe 'copype.cmd'
$reclaimMedia = Join-Path $reclaimPe 'MakeWinPEMedia.cmd'
$reclaimPackages = Join-Path $reclaimPe 'amd64/WinPE_OCs'
foreach ($reclaimRequired in @($reclaimEnvironment, $reclaimCopype, $reclaimMedia, (Join-Path $PSScriptRoot 'ReclaimRescue.exe'))) {
    if (-not (Test-Path -LiteralPath $reclaimRequired)) { throw "Required component missing: $reclaimRequired. Install Windows ADK Deployment Tools and the matching Windows PE add-on, or extract the full Reclaim release." }
}
$reclaimOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $reclaimOutput) { throw 'Choose a NEW output directory. Existing folders are never replaced.' }
if ($reclaimOutput -match '["&|<>^%!\r\n]') { throw 'Choose a path without command-shell special characters.' }
New-Item -ItemType Directory -Path $reclaimOutput | Out-Null
$reclaimWork = Join-Path $reclaimOutput 'WinPE'
$reclaimMount = Join-Path $reclaimOutput 'Mount'
$reclaimIso = Join-Path $reclaimOutput 'Reclaim-Rescue-x64.iso'
function Invoke-ReclaimAdk([string]$Script, [string]$Arguments) {
    & $env:ComSpec /d /v:off /s /c "`"`"$reclaimEnvironment`" && `"$Script`" $Arguments`""
    if ($LASTEXITCODE -ne 0) { throw "ADK media operation failed (exit $LASTEXITCODE)." }
}
function Invoke-ReclaimDism([string[]]$Arguments) {
    & dism.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WinPE servicing failed (exit $LASTEXITCODE)." }
}
Invoke-ReclaimAdk $reclaimCopype "amd64 `"$reclaimWork`""
New-Item -ItemType Directory -Path $reclaimMount | Out-Null
$reclaimWim = Join-Path $reclaimWork 'media/sources/boot.wim'
$reclaimMounted = $false
try {
    Invoke-ReclaimDism @('/Mount-Image', "/ImageFile:$reclaimWim", '/Index:1', "/MountDir:$reclaimMount")
    $reclaimMounted = $true
    foreach ($reclaimPackage in @('WinPE-WMI', 'WinPE-NetFX')) {
        $reclaimCab = Join-Path $reclaimPackages "$reclaimPackage.cab"
        if (-not (Test-Path -LiteralPath $reclaimCab)) { throw "Matching x64 WinPE optional component is missing: $reclaimCab" }
        Invoke-ReclaimDism @("/Image:$reclaimMount", '/Add-Package', "/PackagePath:$reclaimCab")
        $reclaimLanguage = Join-Path $reclaimPackages "en-us/$reclaimPackage`_en-us.cab"
        if (Test-Path -LiteralPath $reclaimLanguage) { Invoke-ReclaimDism @("/Image:$reclaimMount", '/Add-Package', "/PackagePath:$reclaimLanguage") }
    }
    $reclaimRescueDir = Join-Path $reclaimMount 'Reclaim'
    New-Item -ItemType Directory -Path $reclaimRescueDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReclaimRescue.exe'), (Join-Path $PSScriptRoot 'README.md') -Destination $reclaimRescueDir
    [IO.File]::WriteAllText((Join-Path $reclaimMount 'Windows/System32/startnet.cmd'), "@echo off`r`nwpeinit`r`nX:\Reclaim\ReclaimRescue.exe`r`necho Recovery console closed. Type X:\Reclaim\ReclaimRescue.exe to run again.`r`n", [Text.Encoding]::ASCII)
    Invoke-ReclaimDism @('/Unmount-Image', "/MountDir:$reclaimMount", '/Commit')
    $reclaimMounted = $false
    Invoke-ReclaimAdk $reclaimMedia "/ISO `"$reclaimWork`" `"$reclaimIso`""
    if (-not (Test-Path -LiteralPath $reclaimIso)) { throw 'ADK did not produce an ISO.' }
    Write-Output "Created: $reclaimIso"
    Write-Output 'No USB was formatted. Test the ISO in a VM, then use a media-writing tool to write it to your chosen USB.'
}
finally {
    if ($reclaimMounted) { & dism.exe /Unmount-Image "/MountDir:$reclaimMount" /Discard | Out-Null }
}
