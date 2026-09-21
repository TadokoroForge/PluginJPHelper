namespace PluginJPHelper.Plugins;

internal static class DalamudActProfile
{
    public const string PluginName = "DalamudACT";

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
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
