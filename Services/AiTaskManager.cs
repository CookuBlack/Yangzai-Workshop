using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace YangzaiWorkshop.Services;

/// <summary>AI 任务类型</summary>
public enum AiTaskType { Image, Video }

/// <summary>图片生成引擎：Api=在线大模型，ComfyUI=本地 ComfyUI 服务</summary>
public enum ImageProvider { Api, ComfyUI }

/// <summary>AI 任务状态</summary>
public enum AiTaskStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// AI 生成任务。所有参数在执行时快照保存，任务入队后即使切换小说/章节/关闭窗口也不受影响。
/// </summary>
public class AiTask
{
    public Guid Id { get; } = Guid.NewGuid();
    public AiTaskType Type { get; init; }
    public string Prompt { get; init; } = "";
    /// <summary>展示用描述：如 1024x768 / 1152x768·121帧·24fps / 角色名</summary>
    public string Detail { get; init; } = "";
    public AiTaskStatus Status { get; set; } = AiTaskStatus.Queued;
    /// <summary>进度/状态描述文本（如“排队中”“生成中 45%”）</summary>
    public string StatusText { get; set; } = "排队中";
    public string? Error { get; set; }
    public string? ResultFileName { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    // ---- 执行参数（快照） ----
    public string ApiEndpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string TargetDir { get; init; } = "";
    /// <summary>生成的文件名模板（不含扩展名）</summary>
    public string FileNameBase { get; init; } = "";
    public string ImageSize { get; init; } = "1024x1024";
    /// <summary>图片生成引擎（默认在线 API）</summary>
    public ImageProvider Provider { get; init; } = ImageProvider.Api;
    // ---- ComfyUI 参数（Provider=ComfyUI 时使用） ----
    /// <summary>ComfyUI 工作流 JSON 文件路径（API 格式）</summary>
    public string ComfyWorkflowFile { get; init; } = "";
    /// <summary>参考图（Data URI Base64 数组）：图片任务 1 张=图生图、多张=多图编辑；视频任务=reference 模式 images</summary>
    public List<string>? ReferenceImages { get; init; }
    /// <summary>输出分辨率档位（agnes-video 2.5：720P/960P/2K；Flash 固定 720P）</summary>
    public string VideoSize { get; init; } = "720P";
    /// <summary>输出画幅比例（16:9 / 9:16 / 1:1 / 4:3 / 3:4 / 21:9）</summary>
    public string VideoRatio { get; init; } = "16:9";
    /// <summary>视频时长（秒），agnes-video 2.5 系列支持 4–12</summary>
    public int VideoSeconds { get; init; } = 5;
    /// <summary>参考视频（Data URI Base64 列表）：reference 模式 videos，仅 agnes-video-2.5（非 Flash）支持</summary>
    public List<string>? ReferenceVideos { get; init; }
    public string NovelName { get; init; } = "";
    /// <summary>章节/角色名（展示用）</summary>
    public string ScopeName { get; init; } = "";

    public CancellationTokenSource Cts { get; } = new();
}

/// <summary>
/// 全局 AI 任务队列：任务入队后串行执行（一次只跑一个），
/// 与窗口/页面生命周期完全解耦，可随时查看队列并选择性取消某个任务。
/// </summary>
public static class AiTaskManager
{
    public static ObservableCollection<AiTask> Tasks { get; } = new();

    /// <summary>任务列表变化（新增/状态变化），UI 订阅后刷新队列窗口</summary>
    public static event Action? Changed;

    /// <summary>执行循环是否已在运行（0=否 1=是），用 Interlocked 保证只有一个串行执行器</summary>
    private static int _loopRunning;

    /// <summary>入队一个新任务；若当前没有任务在执行则立即开始</summary>
    public static void Enqueue(AiTask task)
    {
        Tasks.Add(task);
        Changed?.Invoke();
        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) == 0)
            _ = Task.Run(RunNextAsync);
    }

    /// <summary>取消指定任务：排队中的直接取消，运行中的通过令牌中止</summary>
    public static void Cancel(Guid id)
    {
        var task = Find(id);
        if (task == null) return;
        if (task.Status == AiTaskStatus.Queued)
        {
            task.Status = AiTaskStatus.Cancelled;
            task.StatusText = "已取消";
            Changed?.Invoke();
        }
        else if (task.Status == AiTaskStatus.Running)
        {
            try { task.Cts.Cancel(); } catch { }
            // 状态由执行循环标记
        }
    }

    /// <summary>取消所有排队中/运行中的任务</summary>
    public static void CancelAll()
    {
        foreach (var t in Tasks)
        {
            if (t.Status is AiTaskStatus.Queued)
            {
                t.Status = AiTaskStatus.Cancelled;
                t.StatusText = "已取消";
            }
            else if (t.Status == AiTaskStatus.Running)
            {
                try { t.Cts.Cancel(); } catch { }
            }
        }
        Changed?.Invoke();
    }

    /// <summary>移除所有已结束（完成/失败/取消）的任务，保留排队中与运行中的</summary>
    public static void ClearFinished()
    {
        for (int i = Tasks.Count - 1; i >= 0; i--)
        {
            if (Tasks[i].Status is AiTaskStatus.Completed or AiTaskStatus.Failed or AiTaskStatus.Cancelled)
                Tasks.RemoveAt(i);
        }
        Changed?.Invoke();
    }

    public static AiTask? Find(Guid id)
    {
        foreach (var t in Tasks)
            if (t.Id == id) return t;
        return null;
    }

    /// <summary>串行执行队列：取下一个排队任务并运行，直到队列为空</summary>
    private static async Task RunNextAsync()
    {
        try
        {
            while (true)
            {
                AiTask? next = null;
                foreach (var t in Tasks)
                {
                    if (t.Status == AiTaskStatus.Queued) { next = t; break; }
                }
                if (next == null) return;

                next.Status = AiTaskStatus.Running;
                next.StatusText = "生成中…";
                NotifyChanged(next);

                try
                {
                    await ExecuteAsync(next);
                    next.Status = AiTaskStatus.Completed;
                    next.StatusText = "已完成";
                    NotifyChanged(next);
                    Ui(() => MainWindow.Notify(
                        $"✓ {(next.Type == AiTaskType.Image ? "图片" : "视频")}已生成并保存：{next.ResultFileName}"));
                    RefreshCurrentPage(next);
                }
                catch (OperationCanceledException)
                {
                    next.Status = AiTaskStatus.Cancelled;
                    next.StatusText = "已取消";
                    NotifyChanged(next);
                }
                catch (Exception ex)
                {
                    next.Status = AiTaskStatus.Failed;
                    next.StatusText = "失败";
                    next.Error = ex.Message;
                    NotifyChanged(next);
                    Ui(() => MainWindow.Notify(
                        $"⚠ {(next.Type == AiTaskType.Image ? "图片" : "视频")}生成失败：{ex.Message}", success: false));
                }
                finally
                {
                    try { next.Cts.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _loopRunning, 0);
        }
    }

    /// <summary>执行单个任务（图片或视频），全部在后台线程完成</summary>
    private static async Task ExecuteAsync(AiTask t)
    {
        if (t.Type == AiTaskType.Image)
        {
            if (t.Provider == ImageProvider.ComfyUI)
            {
                await ExecuteComfyImageAsync(t);
            }
            else
            {
                var imageUrl = await ApiService.GenerateImageAsync(
                    t.ApiEndpoint, t.ApiKey, t.Prompt, t.Model, t.ImageSize, t.ReferenceImages, t.Cts.Token);
                var bytes = await ApiService.DownloadImageAsync(imageUrl, t.Cts.Token);
                await SaveAsync(t, bytes, ".png");
            }
        }
        else
        {
            // 有参考图或参考视频 → reference 模式，否则 text 模式（agnes-video 2.5 系列）
            var mode = (t.ReferenceImages is { Count: > 0 } || t.ReferenceVideos is { Count: > 0 })
                ? "reference" : "text";
            var videoId = await ApiService.CreateVideoTaskAsync(
                t.ApiEndpoint, t.ApiKey, t.Model, t.Prompt,
                mode: mode,
                seconds: t.VideoSeconds,
                size: t.VideoSize,
                aspectRatio: t.VideoRatio,
                referenceImages: t.ReferenceImages,
                referenceVideos: t.ReferenceVideos?
                    .Select(v => new VideoReference { Url = v }).ToList(),
                cancel: t.Cts.Token);

            var progress = new Progress<string>(msg =>
            {
                t.StatusText = msg;
                NotifyChanged(t);
            });
            var videoUrl = await ApiService.PollVideoResultAsync(
                t.ApiEndpoint, t.ApiKey, videoId, t.Model, progress, t.Cts.Token);
            var videoBytes = await ApiService.DownloadVideoAsync(videoUrl, t.Cts.Token);
            await SaveAsync(t, videoBytes, ".mp4");
        }
    }

    /// <summary>执行 ComfyUI 本地生图任务（后台线程）：读取工作流 JSON 文件并提交</summary>
    private static async Task ExecuteComfyImageAsync(AiTask t)
    {
        // 解析宽高（ImageSize 形如 "1824x1024"）
        int width = 1024, height = 768;
        try
        {
            var parts = t.ImageSize.Split('x');
            if (parts.Length == 2)
            {
                width = int.Parse(parts[0].Trim());
                height = int.Parse(parts[1].Trim());
            }
        }
        catch { }

        // 参考图：取第一张，去掉 data: 前缀得到纯 base64
        string? refBase64 = null;
        if (t.ReferenceImages is { Count: > 0 })
        {
            var dataUrl = t.ReferenceImages[0];
            var idx = dataUrl.IndexOf(',');
            refBase64 = idx >= 0 ? dataUrl[(idx + 1)..] : dataUrl;
        }

        IProgress<string> progress = new Progress<string>(msg =>
        {
            t.StatusText = msg;
            NotifyChanged(t);
        });

        progress.Report("提交 ComfyUI…");
        var promptId = await ApiService.SubmitComfyWorkflowFileAsync(
            t.ApiEndpoint, t.ComfyWorkflowFile, t.Prompt,
            width, height, refBase64, t.Cts.Token);

        var filename = await ApiService.PollComfyResultAsync(
            t.ApiEndpoint, promptId, progress, t.Cts.Token);

        progress.Report("下载图片…");
        var bytes = await ApiService.DownloadComfyImageAsync(
            t.ApiEndpoint, filename, cancel: t.Cts.Token);

        await SaveAsync(t, bytes, ".png");
    }

    private static async Task SaveAsync(AiTask t, byte[] bytes, string ext)
    {
        t.StatusText = "保存中…";
        NotifyChanged(t);
        try
        {
            if (!Directory.Exists(t.TargetDir)) Directory.CreateDirectory(t.TargetDir);
        }
        catch { }
        var filePath = Path.Combine(t.TargetDir, t.FileNameBase + ext);
        await File.WriteAllBytesAsync(filePath, bytes, t.Cts.Token);
        t.ResultFileName = Path.GetFileName(filePath);
    }

    /// <summary>
    /// 任务完成后实时刷新素材列表：遍历所有缓存页面（含非当前页面），
    /// 由各页面的 TryRefreshAfterAiTask 自己判断目标目录是否匹配。
    /// 这样无论用户停留在哪、是否切走，任务完成都会立即更新对应页面。
    /// </summary>
    private static void RefreshCurrentPage(AiTask task)
    {
        Ui(() =>
        {
            try
            {
                foreach (var page in NavigationService.Instance.AllPages)
                {
                    if (page is Views.ScriptPage sp) sp.TryRefreshAfterAiTask(task);
                    else if (page is Views.CharacterPage cp) cp.TryRefreshAfterAiTask(task);
                    else if (page is Views.VideoPage vp) vp.TryRefreshAfterAiTask(task);
                }
            }
            catch { }
        });
    }

    private static void NotifyChanged(AiTask? task)
    {
        _ = task; // 触发整体刷新
        Ui(() => Changed?.Invoke());
    }

    private static void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) { action(); return; }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }
}
