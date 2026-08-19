<#
.SYNOPSIS
    通用旧版本文件清理工具

.DESCRIPTION
    扫描指定目录，从文件名中提取语义化版本号（如 v1.2.3 / 1.2.3），
    按版本从高到低排序，保留最新的 N 个，删除其余旧版本文件。
    适用于安装包、压缩包、带版本号产物等任意 "name-vX.Y.Z.ext" 命名规律的场景。

.PARAMETER Path
    目标目录（必填）。

.PARAMETER Pattern
    文件匹配通配符，默认 "*"（目录下所有文件）。
    常用："*.msi"、"*.zip"、"*-v*.*"。

.PARAMETER Keep
    保留的最新版本数量，默认 1（仅保留最新版）。

.PARAMETER VersionPattern
    从文件名提取版本号的正则（命名捕获组 ?<v>），默认自动识别 vX.Y.Z 或 X.Y.Z。

.PARAMETER DeleteUnversioned
    默认情况下，无法解析版本号的文件会被保留（以防误删）。
    加此开关可一并删除这些"无名"文件（仅当它们也匹配 Pattern 时）。

.PARAMETER DryRun
    只预览将要删除的文件，不真正删除。建议首次运行先加此开关确认。

.EXAMPLE
    # 预览 output 目录中会被删除的旧版 msi
    .\Clean-OldVersions.ps1 -Path ".\output" -Pattern "*.msi" -DryRun

    # 实际清理，仅保留最新 1 个 msi
    .\Clean-OldVersions.ps1 -Path ".\output" -Pattern "*.msi"

    # 保留最近 3 个版本
    .\Clean-OldVersions.ps1 -Path "D:\releases" -Pattern "*.zip" -Keep 3

    # 清理任意带版本号的 exe，连带删除无法识别版本的文件
    .\Clean-OldVersions.ps1 -Path ".\dist" -Pattern "MyApp-*.exe" -DeleteUnversioned
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$Pattern = "*",

    [int]$Keep = 1,

    [string]$VersionPattern = '(?<v>\d+\.\d+(\.\d+)?)',

    [switch]$DeleteUnversioned,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ---------- 1. 解析目录 ----------
$resolved = Resolve-Path -Path $Path -ErrorAction Stop
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " 旧版本清理工具" -ForegroundColor Cyan
Write-Host " 目录 : $resolved" -ForegroundColor Cyan
Write-Host " 匹配 : $Pattern   保留最新 $Keep 个" -ForegroundColor Cyan
Write-Host " 模式 : $(if ($DryRun) { '预览(DryRun)' } else { '实际删除' })" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

if (-not (Test-Path $resolved -PathType Container)) {
    throw "目录不存在：$resolved"
}

# ---------- 2. 获取文件并提取版本 ----------
$files = Get-ChildItem -Path $resolved -Filter $Pattern -File -ErrorAction SilentlyContinue
if (-not $files) {
    Write-Host "`n未找到匹配的文件，无需清理。" -ForegroundColor Yellow
    return
}

$parsed = @()
$unversioned = @()

foreach ($f in $files) {
    $m = [regex]::Match($f.Name, $VersionPattern)
    if ($m.Success) {
        try {
            $ver = [version]::new($m.Groups['v'].Value)
            # 用 LastWriteTime 作为同版本号时的次级排序键
            $parsed += [PSCustomObject]@{
                File     = $f
                Version  = $ver
                SortKey  = $f.LastWriteTime
                IsLatest = $false
            }
        }
        catch {
            # 版本号格式异常，当作无版本处理
            $unversioned += $f
        }
    }
    else {
        $unversioned += $f
    }
}

# ---------- 3. 排序并标记保留项 ----------
if ($parsed.Count -gt 0) {
    $sorted = $parsed | Sort-Object -Property Version, SortKey -Descending
    $keepCount = [Math]::Min($Keep, $sorted.Count)
    for ($i = 0; $i -lt $sorted.Count; $i++) {
        if ($i -lt $keepCount) { $sorted[$i].IsLatest = $true }
    }
}
else {
    $sorted = @()
}

# ---------- 4. 执行删除 ----------
$toDelete = $sorted | Where-Object { -not $_.IsLatest } | ForEach-Object { $_.File }
if ($DeleteUnversioned) { $toDelete += $unversioned }

if ($toDelete.Count -eq 0) {
    Write-Host "`n没有需要删除的旧版本。" -ForegroundColor Green
    if ($unversioned.Count -gt 0 -and -not $DeleteUnversioned) {
        Write-Host "  (另有 $($unversioned.Count) 个无法识别版本的文件被保留，可用 -DeleteUnversioned 清理)" -ForegroundColor DarkGray
    }
    return
}

Write-Host "`n以下文件将被删除：" -ForegroundColor Yellow
foreach ($f in $toDelete) {
    $size = "{0:N1} KB" -f ($f.Length / 1KB)
    Write-Host "  [删除] $($f.Name)  ($size)" -ForegroundColor Red
}

if ($DryRun) {
    Write-Host "`n[DryRun] 仅预览，未执行删除。去掉 -DryRun 以实际清理。" -ForegroundColor Magenta
    return
}

$deleted = 0
foreach ($f in $toDelete) {
    try {
        Remove-Item $f.FullName -Force
        Write-Host "  已删除: $($f.Name)" -ForegroundColor DarkGray
        $deleted++
    }
    catch {
        Write-Warning "删除失败: $($f.Name) - $_"
    }
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host " 清理完成，共删除 $deleted 个旧版本文件" -ForegroundColor Green
Write-Host " 保留最新 $($sorted.Count - $toDelete.Count + [Math]::Min($Keep, $sorted.Count)) 个版本" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
