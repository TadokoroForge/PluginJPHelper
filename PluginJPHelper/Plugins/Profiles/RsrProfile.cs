namespace PluginJPHelper.Plugins.Profiles;

using Data;

internal sealed class RsrProfile : IPluginProfile
{
    public const string PluginName = "RSR";

    public string Name => PluginName;

    public int SortKey => 0;

    public string DefaultWindowKeyword => "Rotation Solver";

    bool IPluginProfile.MatchesWindow(string windowName) => MatchesWindow(windowName);

    private const string SideBarWindowKeyword = "Rotation Solver Side bar";

    private static readonly string[] WindowKeywords =
    [
        "Rotation Solver Reborn",
        "RotationSolverReborn",
        "Rotation Solver",
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

    public static bool IsSideBarWindow(string capturePlugin, string windowName)
        => MatchesPluginName(capturePlugin)
           && windowName.Contains(SideBarWindowKeyword, StringComparison.OrdinalIgnoreCase);

    public static bool IsNavigationMenu(string visible)
        => RsrNavigationVocabulary.FixedMenus.Contains(visible)
           || RsrNavigationVocabulary.JobMenus.Contains(visible)
           || visible.StartsWith("Duty - ", StringComparison.Ordinal);

    public static bool IsKnownSection(string visible)
        => RsrNavigationVocabulary.KnownSections.Contains(visible);

    public static bool IsShadowKey(string key, IReadOnlyDictionary<string, string> standard)
    {
        if (standard.ContainsKey(key)) return false;

        foreach (var kv in standard)
            if (string.Equals(kv.Value, key, StringComparison.Ordinal))
                return true;

        return false;
    }

    public static bool IsUntranslatableLiteral(string source)
        => source.Contains("##Up", StringComparison.Ordinal)
           || source.Contains("##Down", StringComparison.Ordinal)
           || source.Contains("#####up", StringComparison.OrdinalIgnoreCase)
           || source.Contains("#####down", StringComparison.OrdinalIgnoreCase)
           || source.Contains("Rotation Solver Reborn Remove Territory", StringComparison.Ordinal);

    public static bool IsProgressClickCount(string visible)
        => visible.StartsWith("RSR has helped you by clicking actions ", StringComparison.Ordinal)
           && visible.EndsWith(" times.", StringComparison.Ordinal);
}
