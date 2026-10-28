$ErrorActionPreference = 'Stop'
$testDir = $PSScriptRoot
$root = Split-Path -Parent $testDir
$exe = Join-Path $testDir 'NetworkMonitor.Tests.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler was not found.' }
& $csc /nologo /target:exe /out:"$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "$root\src\NetworkMonitor.cs" "$root\src\Localization.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'One or more self-tests failed.' }

