# ============================================================
#  Gauss Yannin 一键构建打包脚本
#  功能：Release 编译 -> 发布为单文件自包含程序 -> 压缩成便携包
#  产物：DesktopPet/release/GaussYannin_v1.0.0.zip
#  用法：在 PowerShell 中执行  .\build.ps1
#        或直接双击 build.bat
#  说明：--self-contained 使产物无需安装 .NET 运行时即可运行
# ============================================================

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = 'v1.0.0'
$proj    = Join-Path $root 'DesktopPet.csproj'

# 产物目录
$outDir  = Join-Path $root 'release'
$pubDir  = Join-Path $outDir 'publish'
$zipPath = Join-Path $outDir "GaussYannin_${version}.zip"

Write-Host "==> Gauss Yannin 构建开始 ($version)" -ForegroundColor Cyan

# 清理旧产物
Remove-Item $pubDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

# 发布：Release / x64 / 自包含 / 单文件
Write-Host "==> 正在编译并发布..." -ForegroundColor Yellow
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $pubDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败，请检查上方错误信息。" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 打包成 zip（素材已嵌入 exe，删除无用的调试符号，安装包只保留一个 exe）
Write-Host "==> 正在打包..." -ForegroundColor Yellow
Get-ChildItem $pubDir -Filter '*.pdb' | Remove-Item -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pubDir '*') -DestinationPath $zipPath

Write-Host ""
Write-Host "完成！便携安装包已生成：" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "运行方法：解压后双击 DesktopPet.exe，或生成桌面快捷方式。"

# 询问是否打开产物目录
$open = Read-Host "是否打开产物目录？(y/n)"
if ($open -eq 'y' -or $open -eq 'Y') { Explorer.exe $outDir }