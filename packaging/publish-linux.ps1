#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the Avalonia desktop app for Linux x64 and packs it as a .tar.gz.

.DESCRIPTION
    Runs a self-contained single-file `dotnet publish` of src/InterviewAssist.Desktop for linux-x64 into
    publish/linux-x64, then writes publish/InterviewAssist-<Version>-linux-x64.tar.gz containing one folder with
    the `interview-assist` executable (mode 0755), interview-assist.desktop, interview-assist.png and README.txt.

    The archive is written by this script (POSIX ustar + gzip) rather than by a tar tool, because the executable
    bit can't be taken from an NTFS file. Works in Windows PowerShell 5.1 and PowerShell 7 on Windows or Linux.

.PARAMETER Version
    Version in the archive name.

.PARAMETER ArtifactsPath
    Optional --artifacts-path for dotnet (keeps intermediate build files out of the source tree).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging/publish-linux.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0',
    [string]$ArtifactsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/InterviewAssist.Desktop/InterviewAssist.Desktop.csproj'
$linuxFiles = Join-Path $PSScriptRoot 'linux'
$publishRoot = Join-Path $repoRoot 'publish'
$publishDir = Join-Path $publishRoot 'linux-x64'
$packageName = "InterviewAssist-$Version-linux-x64"
$archivePath = Join-Path $publishRoot "$packageName.tar.gz"

# ---------------------------------------------------------------- publish

# A clean folder, so nothing stale from an earlier publish ends up in the archive.
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'linux-x64',
    '--self-contained',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $publishDir
)
if ($ArtifactsPath) {
    $publishArgs += @('--artifacts-path', $ArtifactsPath)
}

# global.json (SDK pin) is found from the current directory.
Push-Location $repoRoot
try {
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$executable = Join-Path $publishDir 'interview-assist'
if (-not (Test-Path $executable)) {
    throw "The published executable was not found at $executable."
}

# ---------------------------------------------------------------- tar.gz

$ascii = [System.Text.Encoding]::ASCII
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function ConvertTo-Octal([long]$Value, [int]$Digits) {
    $text = [Convert]::ToString($Value, 8)
    if ($text.Length -gt $Digits) {
        throw "$Value does not fit in a $Digits-digit tar field."
    }
    return $text.PadLeft($Digits, '0')
}

function Set-HeaderField([byte[]]$Header, [string]$Text, [int]$Offset, [int]$Length) {
    $bytes = $ascii.GetBytes($Text)
    if ($bytes.Length -gt $Length) {
        throw "'$Text' is too long for a $Length-byte tar field."
    }
    [Array]::Copy($bytes, 0, $Header, $Offset, $bytes.Length)
}

# One POSIX ustar header block. $Type: '0' file, '5' directory.
function Write-TarHeader([System.IO.Stream]$Stream, [string]$Name, [long]$Size, [int]$Mode, [char]$Type, [DateTime]$Modified) {
    $header = New-Object byte[] 512
    $mtime = ([DateTimeOffset]$Modified.ToUniversalTime()).ToUnixTimeSeconds()

    Set-HeaderField $header $Name 0 100
    Set-HeaderField $header (ConvertTo-Octal $Mode 7) 100 8
    Set-HeaderField $header (ConvertTo-Octal 0 7) 108 8          # uid
    Set-HeaderField $header (ConvertTo-Octal 0 7) 116 8          # gid
    Set-HeaderField $header (ConvertTo-Octal $Size 11) 124 12
    Set-HeaderField $header (ConvertTo-Octal $mtime 11) 136 12
    Set-HeaderField $header '        ' 148 8                     # checksum is computed with spaces here
    $header[156] = [byte]$Type
    Set-HeaderField $header 'ustar' 257 6
    Set-HeaderField $header '00' 263 2
    Set-HeaderField $header 'root' 265 32                        # uname
    Set-HeaderField $header 'root' 297 32                        # gname

    $checksum = 0
    foreach ($b in $header) {
        $checksum += $b
    }
    Set-HeaderField $header ((ConvertTo-Octal $checksum 6) + [char]0 + ' ') 148 8

    $Stream.Write($header, 0, $header.Length)
}

function Write-TarPadding([System.IO.Stream]$Stream, [long]$Size) {
    $remainder = $Size % 512
    if ($remainder -ne 0) {
        $padding = New-Object byte[] (512 - $remainder)
        $Stream.Write($padding, 0, $padding.Length)
    }
}

function Write-TarFile([System.IO.Stream]$Stream, [string]$Name, [string]$SourcePath, [int]$Mode) {
    $file = Get-Item -LiteralPath $SourcePath
    Write-TarHeader $Stream $Name $file.Length $Mode '0' $file.LastWriteTime
    $source = [System.IO.File]::OpenRead($file.FullName)
    try {
        $source.CopyTo($Stream)
    }
    finally {
        $source.Dispose()
    }
    Write-TarPadding $Stream $file.Length
}

# Text files get Unix line endings and no BOM, whatever the checkout did to them.
function Write-TarTextFile([System.IO.Stream]$Stream, [string]$Name, [string]$SourcePath) {
    $text = [System.IO.File]::ReadAllText($SourcePath) -replace "`r`n", "`n"
    $bytes = $utf8NoBom.GetBytes($text)
    Write-TarHeader $Stream $Name $bytes.Length 420 '0' (Get-Item -LiteralPath $SourcePath).LastWriteTime   # 0644
    $Stream.Write($bytes, 0, $bytes.Length)
    Write-TarPadding $Stream $bytes.Length
}

if (Test-Path $archivePath) {
    Remove-Item -Force $archivePath
}

$output = [System.IO.File]::Create($archivePath)
try {
    $gzip = New-Object System.IO.Compression.GZipStream($output, [System.IO.Compression.CompressionLevel]::Optimal)
    try {
        $folder = "$packageName/"
        Write-TarHeader $gzip $folder 0 493 '5' (Get-Date)                                                   # 0755
        Write-TarFile $gzip "${folder}interview-assist" $executable 493                                      # 0755
        Write-TarFile $gzip "${folder}interview-assist.png" (Join-Path $linuxFiles 'interview-assist.png') 420   # 0644
        Write-TarTextFile $gzip "${folder}interview-assist.desktop" (Join-Path $linuxFiles 'interview-assist.desktop')
        Write-TarTextFile $gzip "${folder}README.txt" (Join-Path $linuxFiles 'README.txt')

        # End of archive: two empty blocks.
        $end = New-Object byte[] 1024
        $gzip.Write($end, 0, $end.Length)
    }
    finally {
        $gzip.Dispose()
    }
}
finally {
    $output.Dispose()
}

Write-Host "Linux package: $archivePath"
