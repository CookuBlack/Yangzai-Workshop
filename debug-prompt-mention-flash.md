# Debug Session: prompt-mention-flash

**Status:** [OPEN]

## 症状 (Symptom)
- 点击提示词中的 `@图片名` 无法更换图片（下拉不出现或选中无效）。
- 图像/提及悬停预览时鼠标光标（或编辑光标）一直闪动。

## 复现步骤
1. 打开 AI 生图/生视频窗口。
2. 添加参考图（右侧资产选中几张）。
3. 在提示词中手动输入 `@`，从下拉选择插入 `@图片名`。
4. 悬停 `@图片名` → 观察光标是否闪动、预览是否出现。
5. 点击 `@图片名` → 观察是否弹出更换下拉框、重选后名称是否变化。

## 环境
- Windows / WPF .NET (C#) 桌面应用
- 文件：Views/PromptMentionBox.cs

## 假设 (Hypotheses)
- **A**: 点击 `@图片名` 时 `HitTestMention`/`_mentionRuns` 未命中目标，`OnEditorMouseUp` 走了 else 分支直接 ClosePopup。
- **B**: 点击时命中了目标，但点击模式 `ShowPopup` 的 candidates 为空，`items.Count==0` 导致提前关闭。
- **C**: 点击模式下拉其实打开了，但锚点偏移导致弹出位置在可视区外/被裁剪，用户看不到点不到。
- **D**: 更换成功但 RebuildDocument 重建后名称未刷新（caret 恢复逻辑或排除映射逻辑影响）。
- **E**: 预览 Popup（独立窗口 + MousePoint）导致光标箭头↔I-beam 反复切换，呈现「一直闪」。

## 插桩点 (Instrumentation)
- A: OnEditorMouseUp — caret、命中 run、分支
- B: ShowPopup — items.Count / clickMode
- C: ShowPopup — offset 与窗口可视范围
- D: SelectCandidate — 命中分支、target 范围、替换前后文本
- E: ShowPreview / ClosePreview — popup IsOpen 与运行计数

## 进度/证据 (Evidence)
- 发现 App.WorkRoot = exe/bin 下的 WorkData（bin\Debug\net8.0-windows\WorkData）。
- 复现后该目录只有 error.log，无本会话 instrumentation 日志 → 判断用户运行的是**旧编译实例**（未加载插桩代码）。
- 依据已知证据（悬停预览可用说明 HitTestMention 命中可靠）应用两处修复：
  - 点击改用鼠标位置命中（HitTestMention）而非点击后光标下标。
  - 预览由独立 MousePoint 弹窗改为控件内右下角叠加层，杜绝窗口在光标处造成指针闪动；并隐藏编辑光标。

## 结论 (Conclusion)
- 待用户在「彻底关闭旧实例 + IDE 重新编译启动」后验证。
- 若仍异常，读取 bin\Debug\net8.0-windows\WorkData\debug-prompt-mention-flash.log 分析 A/B/D/E 插桩点。