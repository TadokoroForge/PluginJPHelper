namespace PluginJPHelper.Plugins.Profiles;

internal interface IPluginProfile
{
    string Name { get; }

    int SortKey { get; }

    string DefaultWindowKeyword { get; }

    bool MatchesWindow(string windowName);
}
