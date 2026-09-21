namespace PluginJPHelper.Plugins;

internal sealed class DalamudActProfile : IPluginProfile
{
    public const string PluginName = "DalamudACT";

    public string Name => PluginName;

    public int SortKey => 10;

    public string DefaultWindowKeyword => PluginName;

    bool IPluginProfile.MatchesWindow(string windowName) => MatchesWindow(windowName);

    private static readonly string[] WindowKeywords =
    [
        "DalamudACT",
        "CombatTimelineWindow",
        "StatusObserverWindow",
        "PartyMonitorWindow",
        "StatsPanelWindow",
        "SkillMonitorWindow",
        "SettingsWindow",
    ];

    public static bool MatchesPluginName(string? name)
        => string.Equals(name, PluginName, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesWindow(string windowName)
    {
        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
