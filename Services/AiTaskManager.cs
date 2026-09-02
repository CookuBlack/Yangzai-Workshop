using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

/// <summary>AI 任务类型</summary>
public enum AiTaskType { Image, Video }

/// <summary>图片生成引擎：Api=在线大模型，ComfyUI=本地 ComfyUI 服务</summary>
public enum ImageProvider { Api, ComfyUI }

/// <summary>AI 任务状态</summary>
public enum AiTaskStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>AI 生成任务。所有参数在执行时快照保存，任务入队后即使切换小说/章节/关闭窗口也不受影响。
/// 实现 INotifyPropertyChanged，任务状态与耗时变化时实时通知队列窗口刷新。</summary>
public class AiTask : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public AiTaskType Type { get; init; }
    public string Prompt { get; init; } = "";
    /// <summary>展示用描述：如 1024x768 / 1152x768·121帧·24fps / 角色名</summary>
    public string Detail { get; init; } = "";
    private AiTaskStatus _status = AiTaskStatus.Queued;
    public AiTaskStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(DurationText));
        }
    }
    private string _statusText = "排队中";
    /// <summary>进度/状态描述文本（如“排队中”“生成中 45%”）</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public string? Error { get; set; }
    public string? ResultFileName { get; set; }
    public DateTime CreatedAt { get; } = DateTime.Now;

    /// <summary>开始执行时间（转为 Running 时记录）</summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>结束时间（完成/失败/取消时记录）</summary>
    public DateTime? FinishedAt { get; set; }
    /// <summary>耗时显示文本（仅已结束且有开始时间的任务），如 "耗时 12.3s" / "耗时 1m 23s"；未结束返回空。</summary>
    public string DurationText
    {
        get
        {
            if (StartedAt is not { } start || FinishedAt is not { } finish) return "";
            var dur = finish - start;
            if (dur < TimeSpan.Zero) return "";
            return dur.TotalMinutes >= 1
                ? $"耗时 {(int)dur.TotalMinutes}m {dur.Seconds}s"
                : $"耗时 {dur.TotalSeconds:0.#}s";
        }
    }
    /// <summary>手动标记已结束（设置结束时间并刷新耗时显示）</summary>
    public void MarkFinished()
    {
        FinishedAt = DateTime.Now;
        OnPropertyChanged(nameof(DurationText));
    }

    // ---- 执行参数（快照） ----
    public string ApiEndpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>API 服务商（图片/视频接口按服务商适配请求格式）</summary>
    public ApiProvider ApiProvider { get; init; } = ApiProvider.Agnes;
    public string TargetDir { get; init; } = "";
    /// <summary>生成的文件名模板（不含扩展名）</summary>
    public string FileNameBase { get; init; } = "";
    public string ImageSize { get; init; } = "1024x1024";
    /// <summary>图片尺寸档位（agnes-image 系列：1K/2K/3K/4K），供档位式 size 请求使用</summary>
    public string ImageLevel { get; init; } = "";
    /// <summary>图片画幅比例（如 16:9），供档位式请求使用</summary>
    public string ImageRatio { get; init; } = "";
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
    /// <summary>参考音频（Data URI Base64 列表）：reference 模式 audios，agnes-video-2.5-flash 最多 3 段</summary>
    public List<string>? ReferenceAudios { get; init; }
    /// <summary>首帧（keyframe 首尾帧模式 first_frame，Data URI Base64）</summary>
    public string? FirstFrame { get; init; }
    /// <summary>尾帧（keyframe 首尾帧模式 last_frame，Data URI Base64）</summary>
    public string? LastFrame { get; init; }
    public string NovelName { get; init; } = "";
    /// <summary>章节/角色名（展示用）</summary>
    public string ScopeName { get; init; } = "";

    /// <summary>用与原任务完全相同的参数构造一个新任务（新 Id、新取消源、状态复位为排队），用于「重新生成」。</summary>
    public static AiTask CreateRetry(AiTask src) => new()
    {
        Type = src.Type,
        Prompt = src.Prompt,
        Detail = src.Detail,
        ApiEndpoint = src.ApiEndpoint,
        ApiKey = src.ApiKey,
        Model = src.Model,
        ApiProvider = src.ApiProvider,
        TargetDir = src.TargetDir,
        FileNameBase = src.FileNameBase,
        ImageSize = src.ImageSize,
        ImageLevel = src.ImageLevel,
        ImageRatio = src.ImageRatio,
        Provider = src.Provider,
        ComfyWorkflowFile = src.ComfyWorkflowFile,
        ReferenceImages = src.ReferenceImages,
        VideoSize = src.VideoSize,
        VideoRatio = src.VideoRatio,
        VideoSeconds = src.VideoSeconds,
        ReferenceVideos = src.ReferenceVideos,
        ReferenceAudios = src.ReferenceAudios,
        FirstFrame = src.FirstFrame,
        LastFrame = src.LastFrame,
        NovelName = src.NovelName,
        ScopeName = src.ScopeName
    };

    public CancellationTokenSource Cts { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

    /// <summary>重新生成已完成/失败的任务：用相同参数新建一个任务并重新入队（移除原条目）。</summary>
    public static void Retry(Guid id)
    {
        AiTask? src = null;
        foreach (var t in Tasks) { if (t.Id == id) { src = t; break; } }
        if (src == null) return;
        if (src.Status is not (AiTaskStatus.Completed or AiTaskStatus.Failed or AiTaskStatus.Cancelled)) return;
        var clone = AiTask.CreateRetry(src);
        Tasks.Remove(src);
        Enqueue(clone);
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
                next.StartedAt = DateTime.Now;
                NotifyChanged(next);

                // 读取视频重试配置（仅视频任务失败后自动重试，参数可在设置中修改）
                int maxAttempts = 1;
                int retryIntervalSec = 60;
                bool retryEnabled = false;
                if (next.Type == AiTaskType.Video)
                {
                    try
                    {
                        var cfg = FileService.LoadConfig(App.WorkRoot);
                        retryEnabled = cfg.VideoRetryEnabled;
                        maxAttempts = Math.Max(1, cfg.VideoRetryMaxAttempts);
                        retryIntervalSec = Math.Max(0, cfg.VideoRetryIntervalSeconds);
                    }
                    catch { }
                }

                Exception? lastError = null;
                try
                {
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            await ExecuteAsync(next);
                            lastError = null;
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            if (!retryEnabled || attempt >= maxAttempts) break;
                            // 失败后等待间隔再自动重试，状态文本提示剩余次数
                            next.Error = ex.Message;
                            next.StatusText = $"失败，{retryIntervalSec} 秒后自动重试（第 {attempt}/{maxAttempts} 次）…";
                            NotifyChanged(next);
                            try { await Task.Delay(TimeSpan.FromSeconds(retryIntervalSec), next.Cts.Token); }
                            catch (OperationCanceledException) { throw; }
                            next.StatusText = $"重试中（第 {attempt + 1}/{maxAttempts} 次）…";
                            NotifyChanged(next);
                        }
                    }

                    if (lastError == null)
                    {
                        next.Status = AiTaskStatus.Completed;
                        next.StatusText = "已完成";
                        next.MarkFinished();
                        NotifyChanged(next);
                        Ui(() => MainWindow.Notify(
                            $"✓ {(next.Type == AiTaskType.Image ? "图片" : "视频")}已生成并保存：{next.ResultFileName}"));
                        RefreshCurrentPage(next);
                    }
                    else
                    {
                        next.Status = AiTaskStatus.Failed;
                        next.StatusText = "失败";
                        next.Error = lastError.Message;
                        next.MarkFinished();
                        NotifyChanged(next);
                        Ui(() => MainWindow.Notify(
                            $"⚠ {(next.Type == AiTaskType.Image ? "图片" : "视频")}生成失败：{lastError.Message}", success: false));
                    }
                }
                catch (OperationCanceledException)
                {
                    next.Status = AiTaskStatus.Cancelled;
                    next.StatusText = "已取消";
                    next.MarkFinished();
                    NotifyChanged(next);
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
                    t.ApiEndpoint, t.ApiKey, t.Prompt, t.Model,
                    provider: t.ApiProvider,
                    size: string.IsNullOrEmpty(t.ImageLevel) ? t.ImageSize : t.ImageLevel,
                    referenceImages: t.ReferenceImages,
                    ratio: t.ImageRatio,
                    cancel: t.Cts.Token);
                var bytes = await ApiService.DownloadImageAsync(imageUrl, t.Cts.Token);
                await SaveAsync(t, bytes, ".png");
            }
        }
        else
        {
            // 首尾帧 → keyframe 模式；否则有参考图/参考视频/参考音频 → reference 模式，否则 text 模式（agnes-video 2.5 系列）
            var hasKeyframes = !string.IsNullOrWhiteSpace(t.FirstFrame) || !string.IsNullOrWhiteSpace(t.LastFrame);
            var hasAnyMedia = t.ReferenceImages is { Count: > 0 }
                              || t.ReferenceVideos is { Count: > 0 }
                              || t.ReferenceAudios is { Count: > 0 };
            var mode = hasKeyframes
                ? "keyframe"
                : hasAnyMedia ? "reference" : "text";
            var videoId = await ApiService.CreateVideoTaskAsync(
                t.ApiEndpoint, t.ApiKey, t.Model, t.Prompt,
                mode: mode,
                seconds: t.VideoSeconds,
                size: t.VideoSize,
                aspectRatio: t.VideoRatio,
                firstFrame: t.FirstFrame,
                lastFrame: t.LastFrame,
                referenceImages: t.ReferenceImages,
                referenceAudios: t.ReferenceAudios,
                referenceVideos: t.ReferenceVideos?
                    .Select(v => new VideoReference { Url = v }).ToList(),
                provider: t.ApiProvider,
                cancel: t.Cts.Token);

            var progress = new Progress<string>(msg =>
            {
                t.StatusText = msg;
                NotifyChanged(t);
            });
            var videoUrl = await ApiService.PollVideoResultAsync(
                t.ApiEndpoint, t.ApiKey, videoId, t.Model, progress,
                provider: t.ApiProvider,
                cancel: t.Cts.Token);
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
