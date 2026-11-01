$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler was not found.' }
$webview = Join-Path $root 'vendor\WebView2'
foreach ($required in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.x86.dll', 'WebView2Loader.x64.dll', 'LICENSE.txt', 'NOTICE.txt')) {
    if (-not (Test-Path (Join-Path $webview $required))) { throw "Missing bundled WebView2 component: $required" }
}
$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$version = [regex]::Match((Get-Content -Raw (Join-Path $root 'src\AppVersion.cs')), 'AssemblyInformationalVersion\("(?<version>\d+\.\d+\.\d+)"\)').Groups['version'].Value
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Could not read the application version from src\AppVersion.cs.' }
& $csc /nologo /target:winexe /out:"$outDir\NetworkMonitor.exe" /win32icon:"$root\assets\NetworkMonitor.ico" /win32manifest:"$root\src\NetworkMonitor.exe.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:"$webview\Microsoft.Web.WebView2.Core.dll" /reference:"$webview\Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\Microsoft.Web.WebView2.Core.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.Core.dll" /resource:"$webview\Microsoft.Web.WebView2.WinForms.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\WebView2Loader.x86.dll,NetworkMonitorApp.WebView2.x86.WebView2Loader.dll" /resource:"$webview\WebView2Loader.x64.dll,NetworkMonitorApp.WebView2.x64.WebView2Loader.dll" "$root\src\AppVersion.cs" "$root\src\AutoUpdater.cs" "$root\src\NetworkMonitor.cs" "$root\src\SpeedTestPage.cs" "$root\src\Localization.cs" "$root\src\ChatGptPlanClient.cs" "$root\src\AiProviderClient.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed with compiler exit code $LASTEXITCODE." }
$distZip = Join-Path $outDir 'NetworkMonitor-Portable.zip'
$repoZip = Join-Path $root 'NetworkMonitor-Portable.zip'
Compress-Archive -Path (Join-Path $outDir 'NetworkMonitor.exe'), (Join-Path $root 'README.txt'), (Join-Path $root 'THIRD-PARTY-NOTICES.txt'), (Join-Path $webview 'LICENSE.txt'), (Join-Path $webview 'NOTICE.txt') -DestinationPath $distZip -Force
Copy-Item -LiteralPath $distZip -Destination $repoZip -Force
if (-not (Test-Path -LiteralPath $distZip) -or (Get-Item -LiteralPath $distZip).Length -lt 10000) { throw 'Portable package was not created correctly.' }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $repoZip).Hash.ToLowerInvariant()
$manifest = [ordered]@{ version = $version; sha256 = $hash } | ConvertTo-Json -Compress
Set-Content -LiteralPath (Join-Path $root 'update.json') -Value $manifest -Encoding UTF8
Write-Host "Built $outDir\NetworkMonitor.exe"
Write-Host "Packaged $repoZip"
Write-Host "Generated update manifest for version $version (SHA-256 $hash)"

