using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopPet
{
    /// <summary>用户可调设置的本地持久化（JSON），记录用户调整过的值，下次启动自动恢复。</summary>
    public static class PetSettings
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopPet", "settings.json");

        // 注册表 Run 项中的键名（开机自启动）
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "DesktopPet";

        public sealed class Data
        {
            public double Size { get; set; } = 200;      // 宠物大小（逻辑像素）
            public double RunSpeed { get; set; } = 5.0;  // 奔跑速度
            public int HerdCount { get; set; } = 3;      // 小羊数量
            public bool ShowSeconds { get; set; } = true; // 时钟是否显示秒
            public bool Use24Hour { get; set; } = true;   // 24 / 12 小时制
            public bool AutoStart { get; set; } = false;  // 是否开机自启动
            public int PomoWorkMin { get; set; } = 15;    // 番茄钟工作时长（分钟）
            public int PomoBreakMin { get; set; } = 5;    // 番茄钟休息时长（分钟）
        }

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true
        };

        public static Data Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    Log($"读取设置 <- {json.Replace("\n", " ")}");
                    var d = JsonSerializer.Deserialize<Data>(json, Opts);
                    if (d != null) return d;
                }
                else
                {
                    Log("设置文件不存在，使用默认值");
                }
            }
            catch (Exception ex) { Log($"读取设置失败 -> {ex.Message}"); }
            return new Data();
        }

        public static void Save(Data data)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(data, Opts);
                File.WriteAllText(FilePath, json);
                Log($"写入设置 OK -> {json.Replace("\n", " ")}");
            }
            catch (Exception ex) { Log($"写入设置失败 -> {ex.Message}"); }
        }

        /// <summary>追加一行设置日志到 settings.log（与 settings.json 同目录），用于排查持久化问题。</summary>
        public static void Log(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath) ?? ".";
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "settings.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { /* 日志写入失败不影响运行 */ }
        }

        // ---------- 开机自启动（注册表 HKCU\...\Run） ----------

        /// <summary>当前是否已注册开机自启动。</summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(StartupValueName) != null;
            }
            catch (Exception ex) { Log($"读取自启动状态失败 -> {ex.Message}"); return false; }
        }

        /// <summary>设置或取消开机自启动。</summary>
        public static void SetAutoStart(bool enable)
        {
            try
            {
                var exePath = Environment.ProcessPath
                    ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) { Log("无法获取程序路径，跳过自启动设置"); return; }

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (enable)
                    key?.SetValue(StartupValueName, $"\"{exePath}\"");
                else
                    key?.DeleteValue(StartupValueName, false);
                Log($"设置自启动 -> {(enable ? "开启" : "关闭")} ({exePath})");
            }
            catch (Exception ex) { Log($"设置自启动失败 -> {ex.Message}"); }
        }
    }
}
