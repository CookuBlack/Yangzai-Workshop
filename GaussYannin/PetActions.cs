using System;

namespace DesktopPet
{
    /// <summary>
    /// 宠物回调节点：宠物作为独立类库无法反向引用主程序，
    /// 由主程序在启动时把这些委托指向真实实现，从而打通“宠物 → 主程序”的调用。
    /// </summary>
    public static class PetActions
    {
        /// <summary>音乐播放 / 暂停</summary>
        public static Action? ToggleMusic;

        /// <summary>打开 AI 生成图片窗口</summary>
        public static Action? OpenGenerateImage;

        /// <summary>打开 AI 生成视频窗口</summary>
        public static Action? OpenGenerateVideo;

        /// <summary>打开 AI 对话窗口</summary>
        public static Action? OpenChat;

        /// <summary>查看 AI 任务队列</summary>
        public static Action? OpenQueue;

        /// <summary>打开宠物资源管理窗口</summary>
        public static Action? OpenResources;
    }
}