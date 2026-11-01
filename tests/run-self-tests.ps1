$ErrorActionPreference = 'Stop'
$testDir = $PSScriptRoot
$root = Split-Path -Parent $testDir
$exe = Join-Path $testDir 'NetworkMonitor.Tests.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler was not found.' }
$webview = Join-Path $root 'vendor\WebView2'
& $csc /nologo /target:exe /out:"$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:"$webview\Microsoft.Web.WebView2.Core.dll" /reference:"$webview\Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\Microsoft.Web.WebView2.Core.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.Core.dll" /resource:"$webview\Microsoft.Web.WebView2.WinForms.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\WebView2Loader.x86.dll,NetworkMonitorApp.WebView2.x86.WebView2Loader.dll" /resource:"$webview\WebView2Loader.x64.dll,NetworkMonitorApp.WebView2.x64.WebView2Loader.dll" "$root\src\AppVersion.cs" "$root\src\AutoUpdater.cs" "$root\src\NetworkMonitor.cs" "$root\src\SpeedTestPage.cs" "$root\src\Localization.cs" "$root\src\ChatGptPlanClient.cs" "$root\src\AiProviderClient.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'One or more self-tests failed.' }
