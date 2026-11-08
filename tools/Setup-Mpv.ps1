# Downloads a pinned upstream archive and extracts data only. Never runs mpv or an installer.
# Use -Offline to require an already downloaded tools/mpv-ci.zip.
[CmdletBinding()]
param([switch]$Offline)

$ErrorActionPreference = 'Stop'
$setupRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$archivePath = Join-Path $setupRoot 'mpv-ci.zip'
$playerRoot = Join-Path $setupRoot 'mpv'
$downloadUrl = 'https://github.com/mpv-player/mpv/releases/download/git-release/mpv-v0.42.0-dev-g55d3feb45-42342927321-x86_64-w64-mingw32-full.zip'
$archiveHash = 'D694C039F2B8BE41E876ACBCD9F3E38F87F9320297EEEC2106A79BC3F769BBE8'
$archiveLength = 65712042L
$members = @(
    @{ Name='mpv.exe'; Length=159673344L; Hash='B069DDAD2ED9AF47C89E2D626A7478CD56841CA8A5129DD79410BF1FD1A2415C' },
    @{ Name='LICENSE.GPL'; Length=17984L; Hash='EDAEF632CBB643E4E7A221717A6C441A4C1A7C918E6E4D56DEBC3D8739B233F6' }
)

function Assert-LocalPath([string]$Path) {
    $checkedPath = [IO.Path]::GetFullPath($Path)
    if ($checkedPath.StartsWith('\\', [StringComparison]::Ordinal)) { throw 'Use a local drive; network paths are unsupported.' }
    $checkedParent = $checkedPath
    while ($checkedParent) {
        if (Test-Path -LiteralPath $checkedParent) {
            $checkedItem = Get-Item -LiteralPath $checkedParent -Force
            if (($checkedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Refusing a linked path: ' + $checkedParent) }
        }
        $checkedParent = [IO.Path]::GetDirectoryName($checkedParent)
    }
    return $checkedPath
}

function Assert-OwnedPath([string]$Path) {
    $checkedPath = Assert-LocalPath $Path
    $rootPrefix = $setupRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $checkedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Setup path escaped the tools directory.' }
    return $checkedPath
}

function Assert-File([string]$Path, [long]$Length, [string]$Hash) {
    $null = Assert-OwnedPath $Path
    if (-not [IO.File]::Exists($Path)) { throw ('Required file is missing: ' + $Path) }
    if ((Get-Item -LiteralPath $Path).Length -ne $Length -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw ('File verification failed; the existing file was preserved: ' + $Path)
    }
}

function Receive-PinnedArchive([string]$Destination) {
    $null = Assert-OwnedPath $Destination
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $request = [Net.HttpWebRequest]::Create($downloadUrl)
    $request.UserAgent = '4090M-Studio/1.0'
    $request.Timeout = 25000
    $request.ReadWriteTimeout = 25000
    $response = $null; $inputStream = $null; $outputStream = $null
    try {
        $response = $request.GetResponse()
        if ($response.ContentLength -gt $archiveLength) { throw 'Upstream archive is larger than the pinned version.' }
        $inputStream = $response.GetResponseStream()
        $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        $total = 0L
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $count
            if ($total -gt $archiveLength -or $watch.Elapsed.TotalMinutes -gt 5) { throw 'Download exceeded the pinned length or time limit.' }
            $outputStream.Write($buffer, 0, $count)
        }
        $outputStream.Flush($true)
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
    }
    Assert-File $Destination $archiveLength $archiveHash
}

$null = Assert-LocalPath $setupRoot
$null = Assert-OwnedPath $archivePath
$null = Assert-OwnedPath $playerRoot
if (-not [IO.Directory]::Exists($setupRoot)) { throw 'Run this script from the project tools directory.' }

$hasher = [Security.Cryptography.SHA256]::Create()
try { $mutexKey = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($setupRoot.ToUpperInvariant())))).Replace('-', '') } finally { $hasher.Dispose() }
$setupMutex = New-Object Threading.Mutex($false, ('Local\RtxHub_MpvSetup_' + $mutexKey))
$lockHeld = $false
$stage = $null
$partial = $null
try {
    try { $lockHeld = $setupMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $lockHeld = $true }
    if (-not $lockHeld) { throw 'Another mpv setup operation is running.' }
    if ([IO.File]::Exists($archivePath)) {
        Assert-File $archivePath $archiveLength $archiveHash
    } else {
        if ($Offline) { throw ('Offline archive is missing: ' + $archivePath) }
        $partial = Join-Path $setupRoot ('.mpv-download-' + [Guid]::NewGuid().ToString('N') + '.partial')
        Write-Output 'Downloading the pinned mpv CI archive from upstream...'
        Receive-PinnedArchive $partial
        $null = Assert-OwnedPath $archivePath
        [IO.File]::Move($partial, $archivePath)
        $partial = $null
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stage = Join-Path $setupRoot ('.mpv-setup-' + [Guid]::NewGuid().ToString('N'))
    $null = Assert-OwnedPath $stage
    $null = [IO.Directory]::CreateDirectory($stage)
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($member in $members) {
            # Only these two exact flat entries are extracted. Registration scripts,
            # companion launchers, JAR files and all other ZIP paths stay untouched.
            $matches = @($archive.Entries | Where-Object { $_.FullName -ceq $member.Name })
            if ($matches.Count -ne 1 -or $matches[0].Length -ne $member.Length) { throw ('Unexpected archive member: ' + $member.Name) }
            $stagedFile = Join-Path $stage $member.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($matches[0], $stagedFile, $false)
            Assert-File $stagedFile $member.Length $member.Hash
        }
    } finally { $archive.Dispose() }

    # Validate all existing destinations before publishing either file. Never
    # overwrite a modified player, its preferences, or another installation.
    foreach ($member in $members) {
        $destination = Join-Path $playerRoot $member.Name
        $null = Assert-OwnedPath $destination
        if (Test-Path -LiteralPath $destination) { Assert-File $destination $member.Length $member.Hash }
    }
    $null = [IO.Directory]::CreateDirectory($playerRoot)
    foreach ($member in $members) {
        $destination = Join-Path $playerRoot $member.Name
        $null = Assert-OwnedPath $destination
        if (-not [IO.File]::Exists($destination)) { [IO.File]::Move((Join-Path $stage $member.Name), $destination) }
        Assert-File $destination $member.Length $member.Hash
    }
    Write-Output 'mpv and its license are ready under tools/mpv. No executable was run.'
} finally {
    # Flat, unique setup paths only; no recursive deletion or inferred targets.
    if ($stage -and [IO.Directory]::Exists($stage)) {
        $null = Assert-OwnedPath $stage
        foreach ($member in $members) {
            $stagedFile = Join-Path $stage $member.Name
            $null = Assert-OwnedPath $stagedFile
            if ([IO.File]::Exists($stagedFile)) { [IO.File]::Delete($stagedFile) }
        }
        [IO.Directory]::Delete($stage, $false)
    }
    if ($partial -and [IO.File]::Exists($partial)) {
        $null = Assert-OwnedPath $partial
        [IO.File]::Delete($partial)
    }
    if ($lockHeld) { $setupMutex.ReleaseMutex() }
    $setupMutex.Dispose()
}
