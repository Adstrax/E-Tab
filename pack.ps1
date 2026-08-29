#Requires -Version 5.1
<#
.SYNOPSIS
    构建 E-Tab 发布包：单文件 exe + 使用说明，扁平结构。
.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -Version 2.0.0
#>
param(
    [string]$Version = '3.6.0',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$stageDir = Join-Path $artifacts 'stage'
$zipPath = Join-Path $artifacts "E-Tab-$Version-win64.zip"

foreach ($dir in @($publishDir, $stageDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

dotnet publish (Join-Path $root 'E-Tab\E-Tab.csproj') `
    -c Release -r $Runtime --self-contained false `
    /p:PublishSingleFile=true /p:DebugType=None /p:DebugSymbols=false `
    -o $publishDir

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item (Join-Path $publishDir 'E-Tab.exe') $stageDir -Force
Copy-Item (Join-Path $root 'packaging\README.txt') (Join-Path $stageDir 'README.txt') -Force

Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -Force
Write-Host "OK: $zipPath"
