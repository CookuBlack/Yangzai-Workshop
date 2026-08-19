# 安装包打包指南

本目录包含 Yangzai Workshop 的安装包打包配置（WiX Toolset v5）。

## 文件说明

| 文件 | 说明 |
|---|---|
| `Product.wxs` | WiX 安装包主配置（功能、目录、快捷方式、自启动、UI） |
| `build.ps1` | 一键打包脚本（发布 + 编译 MSI） |
| `nuget.config` | NuGet 源配置（首次添加 UI 扩展时自动生成） |

## 功能支持

- ✅ **自定义安装路径**：安装向导 `WixUI_InstallDir` 提供路径选择
- ✅ **桌面快捷方式**：可选功能（默认勾选）
- ✅ **开机自启动**：可选功能（默认不勾选，写入 `HKCU\...\Run` 注册表）
- ✅ **开始菜单快捷方式**：主程序必备
- ✅ **卸载**：通过控制面板或卸载程序完整移除

## 前置条件

1. **WiX Toolset v5**（CLI 版本 7.0.0）：
   ```powershell
   dotnet tool install --global wix
   ```

2. **.NET 8 SDK**（用于发布项目）

3. 首次使用需接受 EULA：
   ```powershell
   wix eula accept wix7
   ```

## 一键打包

```powershell
# 进入 Packaging 目录
cd Packaging

# 完整打包（清理旧版 + 发布 + 编译 MSI）
.\build.ps1

# 跳过发布（已有 publish 目录）
.\build.ps1 -SkipPublish

# 不清理旧版产物
.\build.ps1 -NoClean

# 手动指定版本号（默认从 version.json 读取）
.\build.ps1 -Version "3.5.0"

# 自定义输出路径
.\build.ps1 -Output "D:\output\YangzaiWorkshop.msi"
```

或直接双击 `一键打包.bat`。

产物默认输出到 `Packaging\output\YangzaiWorkshop-v<版本>.msi`。

### 打包流程

| 步骤 | 说明 |
|---|---|
| 1. 清理旧版 | 删除 output 目录中所有旧 `.msi` 文件；清理 publish 目录残留 |
| 2. 同步版本 | 从 `version.json` 读取版本号，自动写入 `Product.wxs` |
| 3. 检查环境 | 接受 WiX EULA，确保 UI 扩展已安装 |
| 4. 发布项目 | `dotnet publish`（framework-dependent） |
| 5. 编译 MSI | `wix build` 生成安装包 |

## 手动打包步骤

如果不使用脚本，手动执行：

```powershell
# 1. 接受 EULA
wix eula accept wix7

# 2. 发布项目（framework-dependent）
dotnet publish YangzaiWorkshop.csproj -c Release -r win-x64 --self-contained false -o publish

# 3. 添加 UI 扩展（首次）
wix extension add WixToolset.UI.wixext

# 4. 编译 MSI
wix build -acceptEula wix7 -ext WixToolset.UI.wixext Product.wxs -o output\YangzaiWorkshop.msi
```

## 版本更新

发版时需更新以下位置的版本号：

| 位置 | 字段 | 说明 |
|---|---|---|
| `version.json` | `latest`、`release_url` | **主版本源**，打包脚本自动读取 |
| `App.xaml.cs` | `CurrentVersion` | 应用运行时版本号 |
| `Models\AppConfig.cs` | `Version` | 配置文件默认版本号 |
| `Packaging\Product.wxs` | `Package Version` | ~~手动更新~~ 打包时自动同步 |
| `Packaging\build.ps1` | ~~`$Version`~~ | 已改为自动读取，无需手动改 |

> 只需更新 `version.json` + 两个代码文件，`Product.wxs` 由打包脚本自动同步。

## 注意事项（WiX v5 语法陷阱）

1. **Codepage="936"**：支持中文，缺少会导致中文乱码
2. **StandardDirectory Id**：
   - 用 `ProgramFiles6432Folder`（不是 `ProgramFiles6432`）
   - 用 `ProgramMenuFolder`（不是 `ProgramMenu6432Folder`）
   - 用 `DesktopFolder`
3. **Files 元素**：不支持 `Exclude` 属性（用精确的 Include 模式）
4. **Feature**：不支持 `Absent` 属性
5. **.NET 8 运行时**：当前为 framework-dependent 发布，目标机器需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。若希望免运行时，可将 `build.ps1` 中 `--self-contained false` 改为 `--self-contained true`（体积会显著增大）。

## 开机自启动说明

安装包中的「开机自启动」功能通过写入注册表实现：

```
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
  名称: YangzaiWorkshop
  值: "C:\Program Files\YangzaiWorkshop\YangzaiWorkshop.exe"
```

用户可在安装向导的「自定义」功能选择界面勾选或取消该功能。
