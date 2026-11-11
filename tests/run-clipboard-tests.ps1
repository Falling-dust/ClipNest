param([switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework 4.x C# compiler was not found.' }
$artifactDir = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$testExe = Join-Path $artifactDir 'ClipboardBridgeTests.exe'
$compilerArgs = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/warn:4',
    '/main:ClipNest.Tests.ClipboardBridgeTests', ('/out:' + $testExe),
    ('/r:' + (Join-Path $frameworkRoot 'WPF\WindowsBase.dll')),
    ('/r:' + (Join-Path $frameworkRoot 'WPF\PresentationCore.dll')),
    ('/r:' + (Join-Path $frameworkRoot 'WPF\PresentationFramework.dll')),
    ('/r:' + (Join-Path $frameworkRoot 'System.Xaml.dll')),
    (Join-Path $projectRoot 'src\HistoryStore.cs'),
    (Join-Path $projectRoot 'src\ClipboardBridge.cs'),
    (Join-Path $PSScriptRoot 'ClipboardBridgeTests.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Clipboard integration test compilation failed ($LASTEXITCODE)." }
Write-Output "Compiled: $testExe"
if (-not $CompileOnly) {
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Isolated clipboard tests failed ($LASTEXITCODE). No fallback to the interactive clipboard was used." }
}
