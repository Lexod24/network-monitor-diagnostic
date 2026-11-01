$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler was not found.' }
$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
& $csc /nologo /target:winexe /out:"$outDir\NetworkMonitor.exe" /win32icon:"$root\assets\NetworkMonitor.ico" /win32manifest:"$root\src\NetworkMonitor.exe.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "$root\src\NetworkMonitor.cs" "$root\src\Localization.cs" "$root\src\ChatGptPlanClient.cs" "$root\src\AiProviderClient.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed with compiler exit code $LASTEXITCODE." }
$distZip = Join-Path $outDir 'NetworkMonitor-Portable.zip'
$repoZip = Join-Path $root 'NetworkMonitor-Portable.zip'
Compress-Archive -Path (Join-Path $outDir 'NetworkMonitor.exe'), (Join-Path $root 'README.txt') -DestinationPath $distZip -Force
Copy-Item -LiteralPath $distZip -Destination $repoZip -Force
if (-not (Test-Path -LiteralPath $distZip) -or (Get-Item -LiteralPath $distZip).Length -lt 10000) { throw 'Portable package was not created correctly.' }
Write-Host "Built $outDir\NetworkMonitor.exe"
Write-Host "Packaged $repoZip"

