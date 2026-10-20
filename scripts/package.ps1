<#
.SYNOPSIS
    构建 LangCode 并产出可分发的 VSIX 安装包。

.DESCRIPTION
    执行 restore + Release 构建，然后把 LangCode.vsix 复制到仓库根目录的 dist/，
    同时生成带版本号的文件与 SHA256SUMS.txt，方便用户直接下载校验。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'dist'
}

$solution = Join-Path $root 'LangCode.sln'
$project = Join-Path $root 'LangCode\LangCode.csproj'
$manifest = Join-Path $root 'LangCode\source.extension.vsixmanifest'

Write-Host '[1/3] 还原依赖...' -ForegroundColor Cyan
& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore 失败' }

Write-Host "[2/3] 构建 $Configuration ..." -ForegroundColor Cyan
& dotnet build $project -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build 失败' }

$vsix = Join-Path $root "LangCode\bin\$Configuration\net472\LangCode.vsix"
if (-not (Test-Path $vsix)) {
    throw "未生成 VSIX：$vsix。请确认 Microsoft.VSSDK.BuildTools 已还原，且构建过程中没有跳过 VSIX 打包。"
}

[xml]$manifestXml = Get-Content -Path $manifest -Encoding UTF8
$version = $manifestXml.PackageManifest.Metadata.Identity.Version

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stable = Join-Path $OutputDirectory 'LangCode.vsix'
$versioned = Join-Path $OutputDirectory "LangCode-$version.vsix"

Copy-Item -Path $vsix -Destination $stable -Force
Copy-Item -Path $vsix -Destination $versioned -Force

$hash = (Get-FileHash -Path $stable -Algorithm SHA256).Hash.ToLowerInvariant()
$sumFile = Join-Path $OutputDirectory 'SHA256SUMS.txt'
"$hash  LangCode.vsix" | Set-Content -Path $sumFile -Encoding ASCII
"$hash  LangCode-$version.vsix" | Add-Content -Path $sumFile -Encoding ASCII

$sizeKb = [math]::Round((Get-Item -Path $stable).Length / 1KB, 1)
Write-Host '[3/3] 打包完成' -ForegroundColor Green
Write-Host "  版本：$version"
Write-Host "  文件：$stable（$sizeKb KB）"
Write-Host "  别名：$versioned"
Write-Host "  校验：$sumFile"
Write-Host "  SHA256：$hash"
Write-Host ''
Write-Host '下一步：' -ForegroundColor Yellow
Write-Host '  · 本机安装：双击 dist\LangCode.vsix'
Write-Host '  · 发布到 Gitee：$env:GITEE_TOKEN = "私人令牌"; powershell -ExecutionPolicy Bypass -File scripts\publish-gitee.ps1'
Write-Host '  · 或者提交 dist/ 后推送，用户可从仓库直接下载'
