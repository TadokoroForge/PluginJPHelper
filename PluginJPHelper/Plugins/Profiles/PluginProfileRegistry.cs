namespace PluginJPHelper.Plugins.Profiles;

internal static class PluginProfileRegistry
{
    private static readonly IPluginProfile[] Profiles =
    [
        new RsrProfile(),
        new BossModRebornProfile(),
        new BossModProfile(),
        new DalamudActProfile(),
    ];

    private static readonly Dictionary<string, IPluginProfile> ByName =
        Profiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);

    public static IReadOnlyList<IPluginProfile> All => Profiles;

    public static IPluginProfile? Find(string pluginName)
        => ByName.GetValueOrDefault(pluginName);

    public static IPluginProfile Resolve(string pluginName, string? customWindowKeyword)
        => Find(pluginName) ?? new GenericPluginProfile(pluginName, customWindowKeyword);

    public static bool MatchesWindow(string pluginName, string windowName, string? customWindowKeyword)
        => Resolve(pluginName, customWindowKeyword).MatchesWindow(windowName);

    public static int SortKey(string pluginName)
        => Find(pluginName)?.SortKey ?? 10;
}
