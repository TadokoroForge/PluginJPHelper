namespace PluginJPHelper.Plugins;

internal sealed class BossModProfile : IPluginProfile
{
    public const string PluginName = "BM";

    private static readonly string[] WindowKeywords = ["BossMod", "Boss Mod"];

    private const string RebornKeyword = "Reborn";

    public string Name => PluginName;

    public int SortKey => 2;

    public string DefaultWindowKeyword => "BossMod";

    public bool MatchesWindow(string windowName)
    {
        if (windowName.Contains(RebornKeyword, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var keyword in WindowKeywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
