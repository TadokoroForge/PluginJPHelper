namespace PluginJPHelper.Plugins;

using System.Runtime.InteropServices;

internal static unsafe class RsrNavigationTracker
{
    [ThreadStatic] private static string? _currentMenu;
    [ThreadStatic] private static string? _currentSection;
    [ThreadStatic] private static string? _pendingMenuCandidate;

    public static string CurrentMenu => _currentMenu ?? string.Empty;

    public static string CurrentSection => _currentSection ?? string.Empty;

    public static void Reset()
    {
        _currentMenu = string.Empty;
        _currentSection = string.Empty;
        _pendingMenuCandidate = string.Empty;
    }

    public static void Observe(byte* label, bool selected, bool captureEnabled, string capturePlugin, string currentWindowName)
    {
        if (!selected || label == null || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        string? raw;
        try
        {
            raw = Marshal.PtrToStringUTF8((nint)label);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(raw)) return;

        var visible = VisibleLabel(raw);
        if (RsrProfile.IsNavigationMenu(visible))
        {
            _pendingMenuCandidate = visible;
            return;
        }

        if (RsrProfile.MatchesWindow(currentWindowName) && RsrProfile.IsKnownSection(visible))
            _currentSection = visible;
    }

    public static void ObserveAfterClick(string raw, bool captureEnabled, string capturePlugin)
    {
        if (string.IsNullOrWhiteSpace(raw) || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        var visible = VisibleLabel(raw);
        if (!RsrProfile.IsNavigationMenu(visible)) return;

        if (!string.Equals(_currentMenu, visible, StringComparison.Ordinal)) _currentSection = string.Empty;
        _pendingMenuCandidate = visible;
        _currentMenu = visible;
    }

    public static void CommitPendingMenu(string capturePlugin)
    {
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;
        if (string.IsNullOrWhiteSpace(_pendingMenuCandidate)) return;

        if (!string.Equals(_currentMenu, _pendingMenuCandidate, StringComparison.Ordinal)) _currentSection = string.Empty;
        _currentMenu = _pendingMenuCandidate;
    }

    private static string VisibleLabel(string source)
    {
        var marker = source.IndexOf("##", StringComparison.Ordinal);
        return marker > 0 ? source[..marker] : source;
    }
}
