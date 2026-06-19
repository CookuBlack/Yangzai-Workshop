using System.Windows.Controls;

namespace YangzaiWorkshop.Services;

public class NavigationService
{
    private static readonly Lazy<NavigationService> _instance = new(() => new NavigationService());
    public static NavigationService Instance => _instance.Value;

    public UserControl? CurrentPage { get; private set; }
    public string CurrentPageName { get; private set; } = "Home";
    public event Action? PageChanged;

    /// <summary>页面缓存：只创建一次，后续直接复用</summary>
    private readonly Dictionary<string, UserControl> _cache = new();

    private NavigationService() { }

    public void NavigateTo(string pageName)
    {
        CurrentPageName = pageName;
        CurrentPage = GetOrCreatePage(pageName);
        PageChanged?.Invoke();
    }

    private UserControl GetOrCreatePage(string pageName)
    {
        if (_cache.TryGetValue(pageName, out var cached))
            return cached;

        var page = CreatePage(pageName);
        _cache[pageName] = page;
        return page;
    }

    private static UserControl CreatePage(string pageName) => pageName switch
    {
        "Profile" => new Views.ProfilePage(),
        "Home" => new Views.HomePage(),
        "Script" => new Views.ScriptPage(),
        "Character" => new Views.CharacterPage(),
        "Video" => new Views.VideoPage(),
        "Stats" => new Views.StatsPage(),
        "Toolbox" => new Views.ToolboxPage(),
        "Settings" => new Views.SettingsPage(),
        _ => new Views.HomePage()
    };

    /// <summary>清除指定页面缓存</summary>
    public void ClearPage(string pageName)
    {
        _cache.Remove(pageName);
    }

    /// <summary>清除所有缓存</summary>
    public void ClearCache()
    {
        _cache.Clear();
    }
}
