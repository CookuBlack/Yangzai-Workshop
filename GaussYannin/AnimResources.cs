using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DesktopPet
{
    /// <summary>
    /// 把 Assets 全部编译进 exe（嵌入资源），运行时从程序域内的资源流直接解码，
    /// 从而让发布产物成为“真正的单文件 exe”，无需在 exe 旁放素材文件。
    /// </summary>
    internal static class AnimResources
    {
        // 嵌入资源统一前缀：DesktopPet.Assets. <相对目录/文件>
        private const string AssetsPrefix = "DesktopPet.Assets.";
        private const string TempDir = "DesktopPet";

        private static readonly Assembly Asm = typeof(AnimResources).Assembly;
        private static readonly string[] AllNames = Asm.GetManifestResourceNames();

        private static readonly object _tempLock = new();

        /// <summary>加载某动画动作的全部帧的原始字节（可在后台线程执行）。</summary>
        public static byte[][] ReadAnimBytes(string folder)
        {
            var prefix = $"{AssetsPrefix}{folder}.{folder}_";
            var names = AllNames
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(FrameNo)
                .ToList();

            var result = new byte[names.Count][];
            for (int i = 0; i < names.Count; i++)
            {
                using var s = Asm.GetManifestResourceStream(names[i]);
                if (s == null) continue;
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                result[i] = ms.ToArray();
            }
            return result;
        }

        /// <summary>从原始字节解码为 BitmapImage（OnLoad + Freeze，可在后台线程安全调用，跨线程使用时已冻结）。</summary>
        public static System.Windows.Media.Imaging.BitmapImage DecodeFrame(byte[] data, int decodePixelWidth)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new System.IO.MemoryStream(data);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        /// <summary>加载某动画动作的全部帧（按帧号排序），并解码为 BitmapImage。</summary>
        /// <param name="folder">动作所在子目录（如 Idle / Walk / Run …）。</param>
        /// <param name="decodePixelWidth">解码宽度（px）：小羊较小可用更低值省内存。</param>
        public static List<System.Windows.Media.Imaging.BitmapImage> LoadAnim(string folder, int decodePixelWidth)
        {
            var frames = new List<System.Windows.Media.Imaging.BitmapImage>();
            // 形如 DesktopPet.Assets.{folder}.{folder}_{序号}.png
            var prefix = $"{AssetsPrefix}{folder}.{folder}_";
            var names = AllNames
                .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(FrameNo)
                .ToList();

            foreach (var name in names)
            {
                using var s = Asm.GetManifestResourceStream(name);
                if (s == null) continue;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = s;                 // OnLoad 模式：EndInit 时会读完整帧
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = decodePixelWidth;
                bmp.EndInit();
                bmp.Freeze();
                frames.Add(bmp);
            }
            return frames;
        }

        /// <summary>把单个嵌入资源提取到临时目录并返回其文件路径（供需要路径的组件如 MediaPlayer 使用）。</summary>
        public static string? ExtractToTemp(string manifestResourceName)
        {
            var all = AllNames;
            var name = all.FirstOrDefault(n =>
                n.Equals(manifestResourceName, StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;

            // 组装相对路径：去掉前缀后，把“目录.目录.”还原为子目录（保留文件扩展名点）
            var rel = name.Substring(AssetsPrefix.Length);          // 如 Idle.Idle_00010.png 或 remind.mp3
            var parts = rel.Split('.');

            // 注意：下面根据段数重新解析文件名/扩展名（不能提前用 parts[^0] 取，
            // ^0 是越界索引会抛 IndexOutOfRangeException，导致所有资源提取失败、提醒音无声）
            string dirRel, fileName, ext;
            if (parts.Length >= 3)
            {
                // 形如 Idle / Idle_00010 / png → 子目录为前 parts[0]，文件为 parts[1]
                dirRel = parts[0];
                fileName = parts[1];
                ext = parts[2];
            }
            else
            {
                // 形如 remind / mp3 → 无子目录
                dirRel = "";
                fileName = parts[0];
                ext = parts[1];
            }

            var targetDir = Path.Combine(Path.GetTempPath(), TempDir, dirRel);
            Directory.CreateDirectory(targetDir);
            var targetFile = Path.Combine(targetDir, $"{fileName}.{ext}");

            lock (_tempLock)
            {
                if (!File.Exists(targetFile))
                {
                    using var src = Asm.GetManifestResourceStream(name);
                    if (src == null) return null;
                    using var dst = File.Create(targetFile);
                    src.CopyTo(dst);
                }
            }
            return targetFile;
        }
        /// <summary>提醒音 remind.mp3 的临时路径（首次调用会从嵌入资源提取）。</summary>
        public static string? RingtoneTempPath() => ExtractToTemp(AssetsPrefix + "remind.mp3");

        /// <summary>托盘图标用帧（Idle_00010.png）的临时路径。</summary>
        public static string? TrayIconTempPath() => ExtractToTemp(AssetsPrefix + "Idle.Idle_00010.png");

        private static long FrameNo(string name)
        {
            // 去掉扩展名后取最后一个 '_' 后的数字
            var baseName = name;
            var dot = baseName.LastIndexOf('.');
            if (dot >= 0) baseName = baseName.Substring(0, dot);
            var idx = baseName.LastIndexOf('_');
            return idx >= 0 && long.TryParse(baseName.Substring(idx + 1), out var n) ? n : 0;
        }
    }
}