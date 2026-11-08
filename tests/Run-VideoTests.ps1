# From the repository root: powershell -ExecutionPolicy Bypass -File .\tests\Run-VideoTests.ps1
# Optional: -MpvPath 'C:\absolute\path\to\mpv.exe'
# Builds only VideoSupport.cs and its self-contained test/helper; never plays media.
param([string]$MpvPath = '',[switch]$SkipMpvProbe)
$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$taskRoot = Split-Path -Parent $testRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.x x64 C# compiler is required.' }
$output = Join-Path $testRoot 'VideoTests.exe'
$compileArguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/utf8output', '/codepage:65001', '/reference:System.dll', '/reference:System.Core.dll', ('/out:' + $output), (Join-Path $taskRoot 'src/VideoSupport.cs'), (Join-Path $testRoot 'VideoTests.cs'))
& $compiler @compileArguments
if ($LASTEXITCODE -ne 0) { throw 'Video test compilation failed.' }
if ($SkipMpvProbe) {
    & $output '--skip-mpv-probe'
} elseif ($MpvPath.Length -gt 0) {
    $resolvedMpv = (Resolve-Path -LiteralPath $MpvPath).Path
    & $output '--mpv' $resolvedMpv
} else {
    & $output
}
if ($LASTEXITCODE -ne 0) { throw 'Video tests failed.' }
