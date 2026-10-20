<#
.SYNOPSIS
    在 Gitee 上创建发行版（Release），并上传 dist/ 下的 VSIX 安装包。

.DESCRIPTION
    需要 Gitee 私人令牌（scope 勾选 projects 即可）：
        $env:GITEE_TOKEN = "你的令牌"
        powershell -ExecutionPolicy Bypass -File scripts\publish-gitee.ps1

    令牌也可以在 https://gitee.com/profile/personal_access_tokens 生成。
    脚本会自动从 git remote 识别 owner/repo，从 VSIX 清单读取版本号作为 tag。

.EXAMPLE
    $env:GITEE_TOKEN = "xxxx"; powershell -ExecutionPolicy Bypass -File scripts\publish-gitee.ps1
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITEE_TOKEN,
    [string]$Tag = '',
    [string]$TargetBranch = 'master',
    [string]$ReleaseName = '',
    [string]$Notes = '',
    [string]$PackageDirectory = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $root 'dist'
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw @'
缺少 Gitee 私人令牌。
请先执行：
    $env:GITEE_TOKEN = "你的私人令牌"
令牌在 https://gitee.com/profile/personal_access_tokens 生成（勾选 projects 权限）。
'@
}

# ── 识别仓库 owner/repo ─────────────────────────────────────────────
$remote = (& git -C $root remote get-url origin).Trim()
if ($remote -notmatch 'gitee\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$') {
    throw "无法从 origin 识别 Gitee 仓库：$remote"
}
$owner = $Matches['owner']
$repo = $Matches['repo']
$api = "https://gitee.com/api/v5/repos/$owner/$repo"

# ── 版本号与安装包 ─────────────────────────────────────────────────
[xml]$manifest = Get-Content -Path (Join-Path $root 'LangCode\source.extension.vsixmanifest') -Encoding UTF8
$version = $manifest.PackageManifest.Metadata.Identity.Version
if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$version" }
if ([string]::IsNullOrWhiteSpace($ReleaseName)) { $ReleaseName = "LangCode $version" }
if ([string]::IsNullOrWhiteSpace($Notes)) {
    $Notes = "LangCode $version 安装包。下载 LangCode.vsix 后双击安装，详见安装文档：https://gitee.com/$owner/$repo/blob/master/docs/install.md"
}

$files = @()
foreach ($name in @('LangCode.vsix', 'SHA256SUMS.txt')) {
    $path = Join-Path $PackageDirectory $name
    if (Test-Path $path) { $files += $path }
}
if ($files.Count -eq 0) {
    throw "在 $PackageDirectory 下没有找到 LangCode.vsix。请先运行 scripts\package.ps1。"
}

# ── 创建发行版；已存在则复用 ───────────────────────────────────────
Write-Host "[1/2] 创建或复用发行版 $Tag ..." -ForegroundColor Cyan
$payload = @{
    access_token     = $Token
    tag_name         = $Tag
    name             = $ReleaseName
    body             = $Notes
    target_commitish = $TargetBranch
    prerelease       = $false
} | ConvertTo-Json -Depth 5
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)

try {
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" `
        -Body $payloadBytes -ContentType 'application/json; charset=utf-8'
    Write-Host "  已创建发行版：$($release.name)"
}
catch {
    Write-Host "  发行版已存在或创建失败，按 tag 查询：$Tag" -ForegroundColor Yellow
    $release = Invoke-RestMethod -Method Get -Uri ($api + '/releases/tags/' + $Tag + '?access_token=' + $Token)
}

if (-not $release.id) {
    throw "无法获取发行版信息：$Tag"
}

# ── 上传附件 ───────────────────────────────────────────────────────
Write-Host '[2/2] 上传安装包...' -ForegroundColor Cyan
foreach ($file in $files) {
    $name = Split-Path -Leaf $file
    $response = & curl.exe -sS -X POST "$api/releases/$($release.id)/attach_files" `
        -F "access_token=$Token" `
        -F "file=@$file"

    if ($LASTEXITCODE -ne 0 -or $response -match '"message"') {
        throw "上传 $name 失败：$response"
    }

    $downloadUrl = "https://gitee.com/$owner/$repo/releases/download/$Tag/$name"
    Write-Host "  已上传：$name"
    Write-Host "  下载地址：$downloadUrl" -ForegroundColor Green
}

Write-Host ''
Write-Host "发行版页面：https://gitee.com/$owner/$repo/releases/tag/$Tag" -ForegroundColor Green
