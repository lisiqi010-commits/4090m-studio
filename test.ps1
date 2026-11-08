param([switch]$WithHardware)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location -LiteralPath $taskRoot
try {
    & (Join-Path $taskRoot 'build.ps1')
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    function Invoke-Check([string]$name,[string[]]$sources,[string[]]$refs=@()) {
        $output=Join-Path $taskRoot ('tests\'+$name+'.exe')
        $argsList=@('/nologo','/platform:x64','/langversion:5','/target:exe',('/main:'+$name),('/out:'+$output))+$refs+$sources
        & $compiler @argsList
        if($LASTEXITCODE -ne 0){throw ($name+' compile failed')}
        & $output
        if($LASTEXITCODE -ne 0){throw ($name+' failed')}
    }
    Invoke-Check 'DriverTests' @('src\DriverProfiles.cs','tests\DriverTests.cs') @('/reference:System.Xml.dll')
    if($WithHardware) {
        & '.\tests\DriverTests.exe' --probe
        if ($LASTEXITCODE -ne 0) { throw 'Read-only NVAPI probe failed' }
        & '.\tests\Run-VideoTests.ps1' -MpvPath (Join-Path $taskRoot 'tools\mpv\mpv.exe')
    } else {
        & '.\tests\Run-VideoTests.ps1' -SkipMpvProbe
    }
    Invoke-Check 'NeuralInstallerTests' @('src\NeuralInstaller.cs','tests\NeuralInstallerTests.cs') @('/reference:System.Xml.dll')
    Invoke-Check 'NeuralPresetsTests' @('src\NeuralPresets.cs','tests\NeuralPresetsTests.cs')
    Invoke-Check 'FrameGenerationPresetsTests' @('src\FrameGenerationPresets.cs','tests\FrameGenerationPresetsTests.cs')
    Invoke-Check 'FrameGenerationSupportTests' @('src\FrameGenerationSupport.cs','src\NeuralInstaller.cs','tests\FrameGenerationSupportTests.cs') @('/reference:System.Xml.dll')
    Invoke-Check 'FrameGenerationInstallerTests' @('src\FrameGenerationInstaller.cs','src\FrameGenerationPresets.cs','src\NeuralInstaller.cs','tests\FrameGenerationInstallerTests.cs') @('/reference:System.Xml.dll')
    Invoke-Check 'FrameGenerationPackagesTests' @('src\FrameGenerationPackages.cs','tests\FrameGenerationPackagesTests.cs')
    Invoke-Check 'NeuralLanguageTests' @('src\NeuralLanguage.cs','src\NeuralInstaller.cs','src\NeuralPresets.cs','tests\NeuralLanguageTests.cs') @('/reference:System.Xml.dll')
    Invoke-Check 'NeuralPackagesTests' @('src\NeuralPackages.cs','tests\NeuralPackagesTests.cs') @('/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
    $playbackSources=@('src\NeuralPlayback.cs','src\NeuralLanguage.cs','src\VideoSupport.cs','src\NeuralPackages.cs','src\NeuralInstaller.cs','src\NeuralPresets.cs')
    $playbackRefs=@('/reference:System.Xml.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
    Invoke-Check 'NeuralPlaybackTests' ($playbackSources+@('tests\NeuralPlaybackTests.cs')) $playbackRefs
    Invoke-Check 'PotPlayerSupportTests' ($playbackSources+@('src\PotPlayerSupport.cs','tests\PotPlayerSupportTests.cs')) $playbackRefs
    Invoke-Check 'WebVideoSupportTests' ($playbackSources+@('src\WebVideoSupport.cs','tests\WebVideoSupportTests.cs')) ($playbackRefs+@('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll'))
    $fgSources=@('src\FrameGenerationPackages.cs','src\FrameGenerationPresets.cs','src\FrameGenerationSupport.cs','src\FrameGenerationInstaller.cs')
    Invoke-Check 'SimpleActionsTests' ($playbackSources+$fgSources+@('src\PotPlayerSupport.cs','src\WebVideoSupport.cs','src\SimpleActions.cs','tests\SimpleActionsTests.cs')) $playbackRefs
    Invoke-Check 'GameLaunchOptionsTests' @('src\GameLaunchOptions.cs','tests\GameLaunchOptionsTests.cs') @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll')
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 /utf8output /define:SIMPLE_HOME_TEST_STUB /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:tests\SimpleHomeTests.exe src\SimpleHome.cs tests\SimpleHomeTests.cs
    if($LASTEXITCODE -ne 0){throw 'Simple home test compilation failed'}
    & '.\tests\SimpleHomeTests.exe'
    if($LASTEXITCODE -ne 0){throw 'Simple home tests failed'}
    $allSources=@(Get-ChildItem -LiteralPath '.\src' -Filter '*.cs'|ForEach-Object {$_.FullName})+@('tests\UiTests.cs')
    Invoke-Check 'UiTests' $allSources @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Xml.dll','/reference:System.Core.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
    $render = Start-Process -FilePath (Join-Path $taskRoot '4090M-Studio.exe') -ArgumentList '--render' -WindowStyle Hidden -Wait -PassThru
    if ($render.ExitCode -ne 0) { throw 'UI render failed' }
    if($WithHardware) {
        $diagnose = Start-Process -FilePath (Join-Path $taskRoot '4090M-Studio.exe') -ArgumentList '--diagnose' -WindowStyle Hidden -Wait -PassThru
        if ($diagnose.ExitCode -ne 0) { throw 'App diagnostic failed' }
    }
    Write-Output 'PASS: full simple-home verification (no game or personal player configuration writes).'
} finally { Pop-Location }


