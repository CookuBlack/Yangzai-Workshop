# ============================================================
#  Yangzai Workshop 一键打包脚本（WiX v5 / wix 7.0.0）
#
#  用法：
#    .\build.ps1              # 清理旧版 + 发布项目 + 编译 MSI
#    .\build.ps1 -SkipPublish # 跳过发布，直接用现有 publish 目录编译
#    .\build.ps1 -NoClean     # 不清理旧版产物
#    .\build.ps1 -Version "3.5.0"  # 手动指定版本号
#    .\build.ps1 -Output "D:\out\app.msi"
#
#  版本号优先级：-Version 参数 > version.json > 默认 3.4.0
#  产物：默认输出到 .\output\YangzaiWorkshop-windows-x64-v<版本>.msi
# ============================================================
param(
    [switch]$SkipPublish,
    [switch]$NoClean,
    [string]$Output = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $Root
$PublishDir = Join-Path $ProjectDir "publish"
$OutputDir = Join-Path $Root "output"

# ============================================================
#  0. 读取版本号
# ============================================================
if (-not $Version) {
    $versionJsonPath = Join-Path $ProjectDir "version.json"
    if (Test-Path $versionJsonPath) {
        $vj = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
        $Version = $vj.latest
        Write-Host "[版本] 从 version.json 读取: v$Version" -ForegroundColor DarkGray
    }
    if (-not $Version) { $Version = "3.4.0" }
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host " Yangzai Workshop 打包 (v$Version)" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# ============================================================
#  1. 清理旧版产物
# ============================================================
if (-not $NoClean) {
    Write-Host "`n[1/5] 清理旧版产物..." -ForegroundColor Yellow

    # --- 清理 output 目录中的旧 MSI / wixpdb ---
    if (Test-Path $OutputDir) {
        # 仅删除「非当前版本」的旧产物，保留目标版本（构建失败也不丢可用安装包）
        # 注意：Get-ChildItem -Path <dir> -Include 在部分 PowerShell 版本下
        # 不加 -Recurse 会返回空，故用 Where-Object 过滤扩展名更可靠
        $targetMsi = "YangzaiWorkshop-windows-x64-v$Version.msi"
        $oldFiles = Get-ChildItem -Path $OutputDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.msi', '.wixpdb') -and $_.Name -ne $targetMsi }
        if ($oldFiles) {
            foreach ($f in $oldFiles) {
                Remove-Item $f.FullName -Force
                Write-Host "  删除旧产物: $($f.Name)" -ForegroundColor DarkGray
            }
        } else {
            Write-Host "  output 目录无旧产物" -ForegroundColor DarkGray
        }
    }

    # 注意：绝不删除 publish 目录！publish/Assets/Carousel 等目录可能存放用户
    # 手动添加的轮播视频/图片（应用内右键「添加轮播视频」写入），dotnet publish
    # 本身只覆盖其生成的文件，不会删用户媒体。此处仅提示，不做任何删除。
    if (-not $SkipPublish -and (Test-Path $PublishDir)) {
        Write-Host "  保留 publish 目录（不删除用户媒体文件）" -ForegroundColor DarkGray
    }
} else {
    Write-Host "`n[1/5] 跳过清理（-NoClean）" -ForegroundColor Gray
}

# ============================================================
#  2. 同步版本号到 Product.wxs
# ============================================================
Write-Host "`n[2/5] 同步版本号到 Product.wxs..." -ForegroundColor Yellow
$wxsPath = Join-Path $Root "Product.wxs"
if (Test-Path $wxsPath) {
    $wxsContent = Get-Content $wxsPath -Raw -Encoding UTF8
    $wxsNew = $wxsContent -replace 'Version="\d+\.\d+\.\d+"', "Version=`"$Version`""
    if ($wxsNew -ne $wxsContent) {
        [System.IO.File]::WriteAllText($wxsPath, $wxsNew, [System.Text.UTF8Encoding]::new($false))
        Write-Host "  Product.wxs Version -> $Version" -ForegroundColor DarkGray
    } else {
        Write-Host "  Product.wxs 版本已一致" -ForegroundColor DarkGray
    }
}

# ============================================================
#  3. 接受 WiX EULA + 检查 UI 扩展
# ============================================================
Write-Host "`n[3/5] 检查 WiX 环境..." -ForegroundColor Yellow

wix eula accept wix7 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "  (EULA 可能已接受，忽略)" -ForegroundColor Gray
}

$uiExt = wix extension list 2>$null | Select-String "WixToolset.UI.wixext"
if (-not $uiExt) {
    Write-Host "  UI 扩展未安装，正在添加..." -ForegroundColor Yellow
    $nugetConfig = Join-Path $Root "nuget.config"
    if (-not (Test-Path $nugetConfig)) {
        @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Out-File -FilePath $nugetConfig -Encoding UTF8
    }
    wix extension add WixToolset.UI.wixext
    if ($LASTEXITCODE -ne 0) { throw "UI 扩展添加失败，请检查网络或 NuGet 源配置" }
} else {
    Write-Host "  UI 扩展已就绪" -ForegroundColor DarkGray
}

# ============================================================
#  4. 发布项目
# ============================================================
if (-not $SkipPublish) {
    Write-Host "`n[4/5] 发布项目 (framework-dependent)..." -ForegroundColor Yellow
    Push-Location $ProjectDir
    try {
        dotnet publish YangzaiWorkshop.csproj -c Release -r win-x64 `
            --self-contained false `
            -o $PublishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
    }
    finally {
        Pop-Location
    }
    Write-Host "  发布完成" -ForegroundColor DarkGray
} else {
    Write-Host "`n[4/5] 跳过发布，使用现有 publish 目录" -ForegroundColor Gray
    if (-not (Test-Path (Join-Path $PublishDir "YangzaiWorkshop.exe"))) {
        throw "publish 目录缺少 YangzaiWorkshop.exe，请先执行 dotnet publish"
    }
}

# ============================================================
#  5. 编译 MSI
# ============================================================
Write-Host "`n[5/5] 编译 MSI..." -ForegroundColor Yellow
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }

$msiPath = if ($Output) { $Output } else { Join-Path $OutputDir "YangzaiWorkshop-windows-x64-v$Version.msi" }

Push-Location $Root
try {
    wix build -acceptEula wix7 `
        -ext WixToolset.UI.wixext `
        -pdbtype none `
        -d Version=$Version `
        Product.wxs `
        -o $msiPath
    if ($LASTEXITCODE -ne 0) { throw "wix build 失败" }
}
finally {
    Pop-Location
}

# ============================================================
#  完成
# ============================================================
$msiSize = if (Test-Path $msiPath) {
    "{0:N1} MB" -f ((Get-Item $msiPath).Length / 1MB)
} else { "未知" }

Write-Host "`n============================================" -ForegroundColor Green
Write-Host " 打包完成！" -ForegroundColor Green
Write-Host " 版本: v$Version" -ForegroundColor Green
Write-Host " 产物: $msiPath" -ForegroundColor Green
Write-Host " 大小: $msiSize" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
