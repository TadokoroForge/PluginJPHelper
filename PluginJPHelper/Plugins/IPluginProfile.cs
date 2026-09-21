namespace PluginJPHelper.Plugins;

internal interface IPluginProfile
{
    string Name { get; }

    int SortKey { get; }

    string DefaultWindowKeyword { get; }

    bool MatchesWindow(string windowName);
}
