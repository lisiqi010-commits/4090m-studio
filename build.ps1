$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 自带的 .NET Framework 4.x 编译器。' }
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$output = Join-Path $taskRoot '4090M-Studio.exe'
& $compiler /nologo /target:winexe /platform:x64 /langversion:5 /optimize+ /utf8output /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.Management.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ('/out:' + $output) $sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output ('已生成: ' + $output)

