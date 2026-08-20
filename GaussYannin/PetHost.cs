using System;
using System.Windows;

namespace DesktopPet
{
    /// <summary>
    /// 宠物生命周期管理器：以单例形式持有主宠物窗口，提供 Show / Hide / Toggle / Close。
    /// 供主程序在工具栏开关宠物时调用，替代原独立 App 的 StartupUri 启动方式。
    /// </summary>
    public static class PetHost
    {
        private static MainWindow? _window;

        public static bool IsVisible => _window != null && _window.IsVisible;

        public static void Show()
        {
            if (_window == null)
            {
                var win = new MainWindow();
                win.Closed += (_, _) => { _window = null; };
                _window = win;
            }

            if (!_window.IsVisible)
            {
                _window.Show();
            }
            else
            {
                _window.Activate();
            }
        }

        public static void Hide()
        {
            if (_window != null && _window.IsVisible)
                _window.Hide();
        }

        public static void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }

        /// <summary>打开宠物设置窗口（若宠物窗口未创建，先创建再打开）</summary>
        public static void OpenSettings()
        {
            Show();
            _window?.OpenSettings();
        }

        public static void Close()
        {
            if (_window == null) return;
            try
            {
                if (_window.IsVisible)
                    _window.Close();
                else
                    _window = null;
            }
            catch
            {
                _window = null;
            }
        }
    }
}