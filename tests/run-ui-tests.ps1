[CmdletBinding()]
param([switch]$CompileOnly, [string]$ApplicationPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) { $ApplicationPath = Join-Path $projectRoot 'dist\ClipNest.exe' }
$applicationPath = [System.IO.Path]::GetFullPath($ApplicationPath)
if (-not (Test-Path -LiteralPath $applicationPath)) { throw 'Build the application with .\build.ps1 first.' }
$artifactDirectory = Join-Path $projectRoot 'artifacts'
$testDirectory = Join-Path $artifactDirectory 'ui-smoke'
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$testPath = Join-Path $testDirectory 'UiSmokeTests.exe'

# The CLR resolves the referenced ClipNest assembly next to this test executable.
Copy-Item -LiteralPath $applicationPath -Destination (Join-Path $testDirectory 'ClipNest.exe') -Force
$compilerArguments = @(
    '/nologo', '/target:exe', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', "/out:$testPath", "/r:$applicationPath",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    ('/r:' + (Join-Path $framework 'WPF\WindowsBase.dll')),
    ('/r:' + (Join-Path $framework 'WPF\PresentationCore.dll')),
    ('/r:' + (Join-Path $framework 'WPF\PresentationFramework.dll')),
    (Join-Path $PSScriptRoot 'UiSmokeTests.cs')
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "UI test compilation failed ($LASTEXITCODE)." }
Write-Host "Built $testPath"
if (-not $CompileOnly) {
    & $testPath (Join-Path $artifactDirectory 'ClipNest-preview.png')
    if ($LASTEXITCODE -ne 0) { throw "UI smoke tests failed ($LASTEXITCODE)." }
}
