namespace PluginJPHelper.Plugins;

internal sealed class GenericPluginProfile(string name, string? windowKeyword) : IPluginProfile
{
    private readonly string[] keywords = (windowKeyword ?? string.Empty)
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Name { get; } = name;

    public int SortKey => 10;

    public string DefaultWindowKeyword => string.Empty;

    public bool MatchesWindow(string windowName)
    {
        foreach (var keyword in keywords)
            if (windowName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
