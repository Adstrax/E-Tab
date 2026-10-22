#Requires -Version 7.0
<#
.SYNOPSIS
    构建 E-Tab 发布包：单文件 exe + 使用说明，扁平结构。
.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -Version 2.0.0
#>
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'E-Tab\E-Tab.csproj'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& dotnet msbuild $project -getProperty:Version).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet msbuild failed with exit code $LASTEXITCODE."
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Could not determine the project version.'
}

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$stageDir = Join-Path $artifacts 'stage'
$zipPath = Join-Path $artifacts "E-Tab-$Version-win64.zip"
$checksumPath = "$zipPath.sha256"
$testExecutablePath = Join-Path $artifacts "E-Tab-$Version-win64.exe"

foreach ($dir in @($publishDir, $stageDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
if (Test-Path $checksumPath) { Remove-Item $checksumPath -Force }
if (Test-Path $testExecutablePath) { Remove-Item $testExecutablePath -Force }

$publishArguments = @(
    $project
    '-c', 'Release'
    '-r', $Runtime
    '--self-contained', 'false'
    '/p:PublishSingleFile=true'
    '/p:DebugType=None'
    '/p:DebugSymbols=false'
    '-o', $publishDir
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}

dotnet publish @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item (Join-Path $publishDir 'E-Tab.exe') $stageDir -Force
Copy-Item (Join-Path $root 'packaging\README.txt') (Join-Path $stageDir 'README.txt') -Force
Copy-Item (Join-Path $publishDir 'E-Tab.exe') $testExecutablePath -Force

Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $checksumPath -Encoding ascii
Write-Host "OK: $zipPath"
Write-Host "TEST EXE: $testExecutablePath"
Write-Host "SHA256: $hash"
