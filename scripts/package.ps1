<#
.SYNOPSIS
    构建 LangCode 并产出 dist\LangCode.vsix。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'LangCode.sln'
$project = Join-Path $root 'LangCode\LangCode.csproj'
$outputDirectory = Join-Path $root 'dist'
$vsix = Join-Path $root "LangCode\bin\$Configuration\net472\LangCode.vsix"

Write-Host '[1/2] 还原依赖...' -ForegroundColor Cyan
& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败' }

Write-Host "[2/2] 构建 $Configuration ..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }

if (-not (Test-Path $vsix)) {
    throw "未生成 VSIX：$vsix"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Copy-Item -Path $vsix -Destination (Join-Path $outputDirectory 'LangCode.vsix') -Force

Write-Host '完成：dist\LangCode.vsix' -ForegroundColor Green
