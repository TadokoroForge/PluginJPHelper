namespace PluginJPHelper.Plugins;

internal sealed class GenericPluginProfile : IPluginProfile
{
    private readonly string[] keywords;

    public GenericPluginProfile(string name, string? windowKeyword)
    {
        Name = name;
        keywords = (windowKeyword ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string Name { get; }

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
