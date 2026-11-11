[CmdletBinding()]
param([switch]$Test, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found. Install .NET Framework 4.8 on Windows, then run this script again.'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactDirectory = Join-Path $projectRoot 'artifacts'
$assetDirectory = Join-Path $projectRoot 'assets'
New-Item -ItemType Directory -Force -Path $outputDirectory, $artifactDirectory, $assetDirectory | Out-Null

# Generate all icon sizes locally, without external assets or packages.
$iconTool = Join-Path $artifactDirectory 'CreateIcon.exe'
$iconPath = Join-Path $assetDirectory 'ClipNest.ico'
& $compiler '/nologo' '/target:exe' '/optimize+' '/r:System.Drawing.dll' "/out:$iconTool" (Join-Path $projectRoot 'tools\CreateIcon.cs')
if ($LASTEXITCODE -ne 0) { throw "Icon tool compilation failed ($LASTEXITCODE)." }
& $iconTool $iconPath
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed ($LASTEXITCODE)." }

$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$applicationPath = Join-Path $outputDirectory 'ClipNest.exe'
$compilerArguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', '/main:ClipNest.App',
    "/out:$applicationPath", "/win32icon:$iconPath",
    ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')),
    ('/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',ClipNest.MainWindow.xaml'),
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Xaml.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    ('/r:' + (Join-Path $framework 'WPF\WindowsBase.dll')),
    ('/r:' + (Join-Path $framework 'WPF\PresentationCore.dll')),
    ('/r:' + (Join-Path $framework 'WPF\PresentationFramework.dll'))
) + $sourceFiles
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "Application compilation failed ($LASTEXITCODE)." }
Write-Host "Built $applicationPath"

if ($Test) {
    $testPath = Join-Path $artifactDirectory 'HistoryStoreTests.exe'
    & $compiler '/nologo' '/target:exe' '/optimize+' '/warn:4' "/out:$testPath" (Join-Path $projectRoot 'src\HistoryStore.cs') (Join-Path $projectRoot 'tests\HistoryStoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "History test compilation failed ($LASTEXITCODE)." }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw "History tests failed ($LASTEXITCODE)." }
}
