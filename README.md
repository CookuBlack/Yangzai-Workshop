# Yangzai Workshop

> 🎬 面向小说漫剧创作者的本地化桌面生产工具，基于 .NET 8 + WPF 开发，覆盖从小说导入、剧本改编、素材管理到多平台数据统计的完整工作流。

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-blue)](https://github.com/dotnet/wpf)
[![License](https://img.shields.io/badge/license-MIT-green)](./LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey)]()
[![Release](https://img.shields.io/badge/release-v4.5.0-C07040)](https://github.com/CookuBlack/Yangzai-Workshop/releases)

![front](./README.assets/front.png)

---

## ✨ 核心特性

- **零数据库依赖** — 文件夹即数据，绿色免安装，复制目录即可完成项目迁移
- **悬浮窗口设计** — `AllowsTransparency` + 大圆角边框 + 深度投影，悬浮于桌面视觉体验
- **智能分章** — 4 种正则并行匹配：`第X部 第Y章` 组合格式 / `第X章` 标准格式 / `序章/番外` 特殊章节 / `Chapter X` 英文格式，中文数字自动转换
- **跟随系统主题** — 切换时 `DynamicResource` 即时更新，页面缓存自动重建
- **完整工作流** — 8 大功能模块，覆盖小说改编全流程
- **现代化 UI** — 自定义无边框悬浮窗口、圆角卡片、导航 ListBox 选中缩放动效、页面淡入淡出过渡
- **文件系统即架构** — `Image\小说\{mediaFolder}\{章节}` / `Video\{mediaFolder}\{章节}` 纯目录结构，MediaFolder 自动防同名碰撞
- **极简技术栈** — 仅引入 1 个第三方图表库（ScottPlot），其余全部使用 .NET 原生能力
- **数据可视化** — 三平台（抖音 / 快手 / Bilibili）折线图，CSV 数据导入
- **安全备份** — 一键 ZIP 备份与恢复，回收站 30 天自动清理，图片/视频内嵌预览
- **自动更新** — 从 GitHub Release 自动检测新版本，MSI 一键下载安装
- **双引擎 AI 生图** — 支持云端 API 与本地 ComfyUI 引擎，可读取自定义工作流 JSON
- **文本历史** — 撤销/重做 + 历史版本回退（类 Word 停顿合并，可配置步数）
- **AI 任务队列** — 视频与图像生成任务统一管理，标题栏实时角标
- **AI 秒传参考素材** — 生成窗口直接拖入图片即自动归入项目资产并作为参考图，资产多选按选择顺序生成
- **AI 生成历史** — 一键回填上次的提示词、参数与参考素材，告别重复输入
- **桌面宠物模式** — 帧动画桌面宠物「小羊」，支持跟随/散步/休息行为，托盘常驻图标，右键菜单一键控制音乐、AI 生图/生视频、任务队列与宠物资源管理

---

## 🛠 技术栈

| 技术组件 | 说明 | 类型 |
|---------|------|------|
| 运行环境 | .NET 8 SDK（`net8.0-windows`） | 系统基础 |
| UI 框架 | WPF (Windows Presentation Foundation) | .NET 原生 |
| 数据序列化 | System.Text.Json | .NET 原生 |
| 图表组件 | [ScottPlot.WPF](https://scottplot.net/) 5.0+ | 唯一第三方库 |
| 图标方案 | Segoe MDL2 Assets 系统字体 | Windows 原生 |
| 架构模式 | 简易 MVVM 分层（Views / Services / Models） | 原生实现 |

---

## 📸 功能概览

### 🏠 首页
- 视频轮播区（双 MediaElement 交叉淡入淡出，支持自动/手动切换）
- 快捷目录入口（根目录 / 图片 / 视频文件夹一键打开）
- 一键开始按钮（快速跳转剧本管理）
- 公告栏 + 版本信息 + 后台音乐播放

### 📖 剧本管理（核心工作区）
- 右侧书籍列表：导入 TXT 小说，自动编码检测（UTF-8/GBK），封面裁剪上传
- 顶部章节导航：4 种正则智能分章 + 手动分章（支持拆分/合并）
- 三栏可拖拽内容区（窗口缩放时等比压缩防溢出）：
  - 小说原内容 — RichTextBox 支持文字高亮标记（5 色）+ 复制
  - 剧本内容 — 可编辑 RichTextBox，失焦自动保存
  - 图像素材 — 3 列网格，支持拖拽导入、大图预览（滚轮缩放+拖拽平移）
- AI 辅助：剧本生成 + 提示词生成（兼容 OpenAI 格式 API）
- AI 生图：支持云端 API 与本地 ComfyUI 双引擎，可配置默认引擎、比例与像素档位
- AI 生成增强：直接拖拽图片进生成窗口自动归入项目资产并作为参考素材；项目资产支持多选参考图并按选择顺序生成；一键回填 AI 生成历史（提示词、参数、参考素材）；默认提示词勾选后自动追加到提示词末尾；提示词优化指令可在设置中自定义；参考图/缩略图异步加载，选中图片与打开窗口不卡顿，最小化可正常收进任务栏；AI 生成素材按时间顺序命名与排序（文件名含毫秒时间戳，同秒多次生成不互相覆盖），素材列表严格按生成时间先后排列
- 文本历史：撤销/重做（Ctrl+Z / Ctrl+Y）+ 历史版本回退，支持停顿合并
- 图像素材瀑布流布局，图片异步加载不卡顿
- 删除小说时同步清除提示词，有剩余小说自动切换到下一部

### 👤 人物素材
- 按小说联动的人物列表，两列网格布局
- 角色头像裁剪上传、性格设定编辑、形象素材管理
- 修改名称即时同步左侧列表 + 持久化保存
- 图片复制保留原始分辨率

### 🎬 视频文件
- 封面横条选择小说 + 章节导航联动，胶卷风格卡片展示
- 视频缩略图自动提取（MediaPlayer 首帧截图，多点位尝试防黑帧）
- 内嵌播放器：播放/暂停、进度条拖拽、双击全屏、空格键控制

### 📊 平台指标
- 抖音 / 快手 / Bilibili 三平台切换
- 播放量、点赞量、评论量三张折线图（ScottPlot）
- CSV 数据导入、手动录入、拖拽排序

### 👤 个人资料
- 头像裁剪上传（即时刷新导航栏小头像）
- 用户名 / 签名编辑
- 个人成就列表（作品卡片 + 统计数据编辑，收益红色高亮）

### 🧰 工具箱
- 小说素材下载跳转、工作目录快速打开
- 回收站管理（还原 / 删除 / 清空 / 图片视频内嵌预览 / 还原即时刷新对应页面）
- 备忘录（独立窗口，2 秒延迟自动保存）

### ⚙️ 设置
- 亮色 / 暗色 / 自定义主题切换（支持跟随系统主题）
- 工作目录配置、字体大小滑块、轮播间隔设置
- 公告编辑、数据 ZIP 备份与恢复
- **自动更新** — 从 GitHub 检测新版本，支持 MSI 一键下载安装

### 🐑 桌面宠物模式
- 帧动画桌面宠物「小羊」，透明置顶悬浮窗口，支持休息 / 散步 / 跟随鼠标三种行为与奔跑速度、大小自定义
- 单击点头、双击跳跃、拖拽移动，退出前挥手告别
- 系统托盘常驻小羊图标，随时一键显示 / 隐藏宠物
- 宠物右键菜单与托盘菜单：**音乐播放 / 暂停**、**AI 对话**、**AI 生图 / 生视频**（支持参考图、提示词优化）、**查看任务队列**、**宠物资源管理**
- 宠物资源管理（工具箱入口）：图片瀑布流缩略图、视频 / 文本卡片，支持预览、定位、删除，可作为 AI 生图 / 生视频参考图素材库

---

## 📁 项目结构

```
Yangzai Workshop/
├── YangzaiWorkshop.sln                # 解决方案文件
├── YangzaiWorkshop.csproj             # 项目文件
├── App.xaml / App.xaml.cs             # 应用入口，启动初始化、自动更新
├── MainWindow.xaml / .cs              # 主窗口（悬浮无边框 + 侧边导航 + 页面过渡）
├── Models/                            # 数据实体模型
│   ├── AppConfig.cs                   # 全局配置（主题、API、音乐、更新）
│   ├── NovelInfo.cs                   # 小说元数据 + 统计数据
│   ├── Chapter.cs                     # 章节（原文 + 剧本 + 提示词）
│   ├── Character.cs                   # 人物角色（性格 + 素材）
│   ├── PlatformStats.cs               # 平台统计（日数据）
│   ├── Memo.cs                        # 备忘录
│   ├── AiTask.cs                      # AI 任务（图片/视频生成参数）
│   ├── HistorySnapshot.cs             # 文本历史快照
│   └── TrashItem.cs                   # 回收站
├── Services/                          # 业务服务层
│   ├── FileService.cs                 # 文件 I/O、JSON 持久化、回收站、备份恢复
│   ├── ChapterParserService.cs        # 智能分章（4 种正则 + 中文数字）
│   ├── ApiService.cs                  # AI API 调用（文生图/生视频/多模态/ComfyUI）
│   ├── AiTaskManager.cs               # AI 任务队列（视频 + 图像统一调度）
│   ├── TextHistoryService.cs          # 文本历史（撤销/重做 + 快照持久化）
│   ├── FloatingWindowManager.cs       # 浮动小窗口管理（最小化隐藏 + 恢复）
│   ├── MusicPlayerService.cs          # 后台音乐播放器
│   ├── NavigationService.cs           # 页面导航 + 缓存管理（单例）
│   ├── ThemeService.cs                # 主题切换（DynamicResource 即时更新）
│   ├── PetService.cs                  # 桌面宠物桥接（音乐/AI/队列/资源回调接入）
│   ├── AiGenHistory.cs                # AI 生成历史记录（提示词/参数/参考素材回填）
│   └── ViewHelpers.cs                 # 通用视图工具（圆角裁切、图片查看器等）
├── Views/                             # 页面视图（UserControl）+ 工具窗口
│   ├── HomePage.xaml/.cs              # 首页
│   ├── ScriptPage.xaml/.cs            # 剧本管理
│   ├── CharacterPage.xaml/.cs         # 人物素材
│   ├── VideoPage.xaml/.cs             # 视频文件
│   ├── StatsPage.xaml/.cs             # 平台指标
│   ├── ToolboxPage.xaml/.cs           # 工具箱
│   ├── ProfilePage.xaml/.cs           # 个人资料
│   ├── SettingsPage.xaml/.cs          # 设置
│   ├── CropWindow.xaml/.cs            # 图片裁剪工具窗口
│   ├── AiTaskQueueWindow.xaml/.cs     # AI 任务队列窗口
│   ├── TextHistoryWindow.xaml/.cs     # 文本历史窗口（撤销/重做/回退）
│   ├── AiGenHistoryWindow.xaml/.cs    # AI 生成历史选择窗口（一键回填）
│   ├── AssetPickerWindow.xaml/.cs     # 项目资产多选选择器
│   ├── InputDialog.xaml/.cs           # 通用输入对话框
│   └── PetResourceWindow.xaml/.cs     # 宠物资源管理（瀑布流预览）
├── GaussYannin/                       # 桌面宠物模块（独立类库 DesktopPet.csproj）
│   ├── MainWindow.xaml/.cs            # 宠物主窗口（帧动画 / 行为 / 拖拽）
│   ├── AnimResources.cs               # 动画资源读取与帧解码
│   ├── PetActions.cs / PetHost.cs     # 回调委托与宠物窗口生命周期
│   ├── PetTray.cs / PetSettings.cs    # 系统托盘图标 / 设置
│   └── Assets/                        # 宠物动画帧（编译进 exe 的嵌入资源）
├── Assets/                            # 静态资源（图标、视频）
├── Resources/                         # 样式与主题
│    ├── Themes/
│    │   ├── LightTheme.xaml            # 亮色主题（9 色 + 阴影）
│    │   └── DarkTheme.xaml             # 暗色主题（9 色 + 阴影）
│    └── Styles/
│        ├── CommonStyles.xaml          # 按钮 / 文本框 / 滚动条 / 导航动画
│        └── CardStyles.xaml            # 卡片阴影与圆角样式
└── version.json                       # 版本信息（自动更新检测用）
```

---

## 🚀 快速开始

### 环境要求

- **操作系统**：Windows 10 1809+ / Windows 11
- **运行时**：[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（自包含发布可免安装）

### 安装方式

#### 方式一：MSI 安装包（推荐）

从 [GitHub Releases](https://github.com/CookuBlack/Yangzai-Workshop/releases) 下载最新 `YangzaiWorkshop-windows-x64-v{version}.msi`，双击安装。

#### 方式二：绿色免安装版

从 [GitHub Releases](https://github.com/CookuBlack/Yangzai-Workshop/releases) 下载 `YangzaiWorkshop-win-x64.zip`，解压后运行 `YangzaiWorkshop.exe`。

#### 方式三：从源码构建

```bash
# 克隆仓库
git clone https://github.com/CookuBlack/Yangzai-Workshop.git
cd Yangzai-Workshop

# 还原依赖并运行
dotnet restore
dotnet run

# 发布为绿色免安装版
dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

---

## 📦 数据存储架构

程序采用**纯本地文件系统存储**，无任何数据库依赖。工作数据默认存放于 `WorkData/` 目录：

```
WorkData/
├── Config/
│   ├── appsettings.json       # 全局配置（主题、API、音乐、更新日期）
│   ├── notice.txt             # 首页公告
│   └── banners/               # 首页轮播视频
├── Novels/
│   └── {novelId}/
│       ├── info.json          # 小说元数据 + 统计数据
│       ├── cover.png          # 小说封面
│       ├── original.txt       # 小说原文
│       ├── chapters.json      # 章节缓存（含剧本 + 提示词）
│       └── Characters/        # 角色信息（头像、性格设定）
├── Image/
│   ├── 人物素材/
│   │   └── {mediaFolder}/     # 按小说 MediaFolder（唯一，防同名碰撞）
│   │       └── {charId}/      # 角色图片素材
│   └── 小说/
│       └── {mediaFolder}/     # 按小说 MediaFolder
│           └── {第X章}/       # 章节配图（按章节分目录）
├── Video/
│   └── {mediaFolder}/         # 按小说 MediaFolder
│       └── {第X章}/           # 章节视频（按章节分目录）
├── Music/                     # 背景音乐文件
├── PetResources/              # 宠物资源（图片 / 视频 / 文本素材）
└── .trash/                    # 回收站（30 天自动清理）
```

---

## 🎨 配色规范

| 资源键 | 亮色模式 | 暗色模式 | 用途 |
|-------|---------|---------|------|
| `WindowBackground` | `#F5EFE6` | `#2B2B2B` | 窗口主背景（暖象牙色） |
| `SidebarBackground` | `#FBF4EA` | `#1F1F1F` | 侧边栏 / 标题栏（暖米色） |
| `CardBackground` | `#FCF9F4` | `#383838` | 卡片面板 |
| `TextPrimary` | `#1A1A1A` | `#FFFFFF` | 主文字 |
| `TextSecondary` | `#666666` | `#AAAAAA` | 次要文字 |
| `PrimaryColor` | `#C07040` | `#C07040` | 品牌主色调（棕色） |
| `BorderColor` | `#D6CBBE` | `#4A4A4A` | 边框分割线 |
| `DangerColor` | `#D32F2F` | `#F44336` | 警示 / 删除 |
| `ShadowColor` | `#35000000` | `#40000000` | 窗口投影 |

![last](./README.assets/last.png)

---

## 📌 更新日志

### v4.5.0（2026-09-02）
- 视频与图片支持「按住拖出复制」：可直接拖到桌面 / 资源管理器复制原文件
- 图片 / 视频播放器双击全屏切换，切换时不重置播放进度、不暂停
- 图片 / 视频新增排序：按名称 / 修改时间 / 创建时间 / 文件大小（升序 / 降序）
- 复制 / 改名 / 删除统一收纳到右键菜单，删除资产增加确认提示
- 视频加载大幅提速：首帧封面持久化缓存 + 受限并行动态补全，首屏秒开
- 视频生成失败自动重试（间隔 / 次数 / 开关可在设置中调整）
- 音乐列表支持滚轮滚动与边界接管，音乐文件夹实时监听、即放即识别
- 自动更新多源对比 + CDN 缓存清除，新版本检测更可靠

---

## 📄 开源协议

本项目基于 [MIT License](./LICENSE) 开源。

---

## 🙏 致谢

- [ScottPlot](https://scottplot.net/) — 轻量级 .NET 图表库
- [Segoe MDL2 Assets](https://docs.microsoft.com/windows/apps/design/style/segoe-ui-symbol-font) — Windows 系统图标字体
