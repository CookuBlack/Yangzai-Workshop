using System.Windows.Controls;

namespace YangzaiWorkshop.Services;

public class NavigationService
{
    private static readonly Lazy<NavigationService> _instance = new(() => new NavigationService());
    public static NavigationService Instance => _instance.Value;

    public UserControl? CurrentPage { get; private set; }
    public string CurrentPageName { get; private set; } = "Home";
    public event Action? PageChanged;

    private NavigationService() { }

    public void NavigateTo(string pageName)
    {
        CurrentPageName = pageName;
        CurrentPage = pageName switch
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
        PageChanged?.Invoke();
    }
}
