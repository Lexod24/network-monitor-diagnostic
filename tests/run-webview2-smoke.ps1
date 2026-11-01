$ErrorActionPreference = 'Stop'
$testDir = $PSScriptRoot
$root = Split-Path -Parent $testDir
$webview = Join-Path $root 'vendor\WebView2'
$buildDir = Join-Path ([System.IO.Path]::GetTempPath()) ('NetworkMonitor-WebView2-Smoke-Build-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $buildDir
$exe = Join-Path $buildDir 'WebView2Smoke.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler was not found.' }
if (-not (Test-Path (Join-Path $webview 'Microsoft.Web.WebView2.Core.dll')) -or -not (Test-Path (Join-Path $webview 'Microsoft.Web.WebView2.WinForms.dll'))) { throw 'Bundled WebView2 SDK assemblies are missing.' }
$loaderName = if ([IntPtr]::Size -eq 8) { 'WebView2Loader.x64.dll' } else { 'WebView2Loader.x86.dll' }
try {
    & $csc /nologo /target:exe /out:"$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:"$webview\Microsoft.Web.WebView2.Core.dll" /reference:"$webview\Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\Microsoft.Web.WebView2.Core.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.Core.dll" /resource:"$webview\Microsoft.Web.WebView2.WinForms.dll,NetworkMonitorApp.WebView2.Microsoft.Web.WebView2.WinForms.dll" /resource:"$webview\WebView2Loader.x86.dll,NetworkMonitorApp.WebView2.x86.WebView2Loader.dll" /resource:"$webview\WebView2Loader.x64.dll,NetworkMonitorApp.WebView2.x64.WebView2Loader.dll" "$testDir\WebView2Smoke.cs"
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 smoke test build failed.' }
    Copy-Item -LiteralPath (Join-Path $webview 'Microsoft.Web.WebView2.Core.dll') -Destination $buildDir
    Copy-Item -LiteralPath (Join-Path $webview 'Microsoft.Web.WebView2.WinForms.dll') -Destination $buildDir
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 runtime smoke test failed.' }
}
finally {
    Remove-Item -LiteralPath $buildDir -Recurse -Force -ErrorAction SilentlyContinue
}
