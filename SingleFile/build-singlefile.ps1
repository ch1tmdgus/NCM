[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\singlefile\NCM_X.exe')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$PublishDirectory = (Resolve-Path $PublishDirectory).Path
foreach ($Name in @('NCM_X.exe','NCM_X.dll','resources.pri','App.xbf','Engine\NcmEngine.exe')) {
    if (-not (Test-Path (Join-Path $PublishDirectory $Name))) { throw "필수 구성 누락: $Name" }
}
$Output = [IO.Path]::GetFullPath($Output)
$Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $Compiler)) { throw 'Windows .NET Framework 4.x C# 컴파일러가 필요합니다.' }
$Scratch = Join-Path ([IO.Path]::GetTempPath()) ('NcmSingleFile-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Scratch | Out-Null
try {
    $ArchivePath = Join-Path $Scratch 'payload.zip'
    $ManifestPath = Join-Path $Scratch 'payload.sha256'
    $Entries = New-Object 'System.Collections.Generic.List[string]'
    $TotalBytes = [long]0
    $KeptBytes = [long]0
    $RemovedFiles = 0
    $FileCount = 0
    $Stream = [IO.File]::Create($ArchivePath)
    $Archive = New-Object IO.Compression.ZipArchive($Stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($File in (Get-ChildItem $PublishDirectory -File -Recurse | Sort-Object FullName)) {
            $Relative = $File.FullName.Substring($PublishDirectory.Length).TrimStart('\').Replace('\','/')
            $TotalBytes += $File.Length
            # Remove only development data. Preserve every application/runtime binary.
            if ($Relative -match '(^|/)(__pycache__|tests)(/|$)' -or
                $Relative -match '\.(pdb|pyc)$' -or
                $Relative -match '^Engine/_internal/(numpy|PIL)/.*\.pyi$' -or
                $Relative -in @('diagnose.ps1','진단 실행.cmd','README.md','.DS_Store')) {
                $RemovedFiles++
                continue
            }
            $KeptBytes += $File.Length
            $FileCount++
            $Hash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $Entries.Add($Hash + '  ' + $Relative)
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Archive, $File.FullName, $Relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $Archive.Dispose(); $Stream.Dispose() }
    [IO.File]::WriteAllLines($ManifestPath, $Entries, (New-Object Text.UTF8Encoding($false)))
    $PackageId = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $LauncherSource = Join-Path $Scratch 'Launcher.cs'
    $Template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Launcher.cs')).Replace('__PACKAGE_ID__', $PackageId)
    [IO.File]::WriteAllText($LauncherSource, $Template, (New-Object Text.UTF8Encoding($true)))
    New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
    & $Compiler /nologo /target:winexe /platform:x64 /optimize+ /debug- /utf8output "/out:$Output" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$ArchivePath,NcmPayload" "/resource:$ManifestPath,NcmManifest" $LauncherSource
    if ($LASTEXITCODE -ne 0) { throw '단일 EXE 빌드 실패' }
    [pscustomobject]@{
        output=$Output
        package_id=$PackageId
        original_bytes=$TotalBytes
        runtime_bytes=$KeptBytes
        exe_bytes=(Get-Item $Output).Length
        removed_development_files=$RemovedFiles
        files=$FileCount
    } | ConvertTo-Json
} finally { Remove-Item $Scratch -Recurse -Force }
