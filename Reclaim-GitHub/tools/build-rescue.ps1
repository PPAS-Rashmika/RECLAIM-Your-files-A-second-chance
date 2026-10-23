param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$reclaimCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $reclaimCompiler)) { throw 'The Windows .NET Framework C# compiler is required to build the WinPE rescue console.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$reclaimRescue = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'ReclaimRescue.exe'
& $reclaimCompiler /nologo /target:exe /platform:x64 /optimize+ /reference:System.Management.dll "/out:$reclaimRescue" (Join-Path $PSScriptRoot 'Rescue.cs')
if ($LASTEXITCODE -ne 0) { throw 'Rescue console compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Bootable/Build-RescueISO.ps1'), (Join-Path $PSScriptRoot 'Bootable/README.md') -Destination $OutputDirectory
