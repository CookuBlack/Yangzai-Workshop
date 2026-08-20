using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopPet
{
    /// <summary>
    /// 宠物常驻托盘图标：独立于宠物窗口生命周期，应用启动即创建，
    /// 提供显示/隐藏宠物、音乐、AI、队列、资源等入口。
    /// </summary>
    public static class PetTray
    {
        private static System.Windows.Forms.NotifyIcon? _icon;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static void Initialize()
        {
            if (_icon != null) return;

            var menu = new System.Windows.Forms.ContextMenuStrip();

            var showHide = new System.Windows.Forms.ToolStripMenuItem("显示/隐藏宠物");
            showHide.Click += (_, _) => PetHost.Toggle();

            var music = new System.Windows.Forms.ToolStripMenuItem("音乐播放/暂停");
            music.Click += (_, _) => PetActions.ToggleMusic?.Invoke();

            var chat = new System.Windows.Forms.ToolStripMenuItem("AI 对话");
            chat.Click += (_, _) => PetActions.OpenChat?.Invoke();

            var resources = new System.Windows.Forms.ToolStripMenuItem("宠物资源");
            resources.Click += (_, _) => PetActions.OpenResources?.Invoke();

            var queue = new System.Windows.Forms.ToolStripMenuItem("查看队列");
            queue.Click += (_, _) => PetActions.OpenQueue?.Invoke();

            var settings = new System.Windows.Forms.ToolStripMenuItem("设置");
            settings.Click += (_, _) => PetHost.OpenSettings();

            var exit = new System.Windows.Forms.ToolStripMenuItem("退出宠物");
            exit.Click += (_, _) => PetHost.Close();

            menu.Items.Add(showHide);
            menu.Items.Add(music);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(chat);
            menu.Items.Add(resources);
            menu.Items.Add(queue);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(settings);
            menu.Items.Add(exit);

            _icon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Gauss Yannin · 桌面宠物",
                ContextMenuStrip = menu,
                Visible = true
            };
            // 双击托盘图标：显示宠物
            _icon.DoubleClick += (_, _) => PetHost.Show();
        }

        public static void Dispose()
        {
            if (_icon == null) return;
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }

        // 从嵌入素材里取一帧渲染成 32x32 托盘图标（透明背景的小羊）
        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var path = AnimResources.TrayIconTempPath();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return System.Drawing.SystemIcons.Application;

                using var bmp = new System.Drawing.Bitmap(path);
                using var small = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(32, 32));
                IntPtr h = small.GetHicon();
                try
                {
                    using var temp = System.Drawing.Icon.FromHandle(h);
                    return (System.Drawing.Icon)temp.Clone();
                }
                finally { DestroyIcon(h); }
            }
            catch { return System.Drawing.SystemIcons.Application; }
        }
    }
}
