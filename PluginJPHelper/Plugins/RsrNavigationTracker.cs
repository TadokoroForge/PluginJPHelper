namespace PluginJPHelper.Plugins;

using System.Runtime.InteropServices;

internal static unsafe class RsrNavigationTracker
{
    [ThreadStatic] private static string? currentMenu;
    [ThreadStatic] private static string? currentSection;
    [ThreadStatic] private static string? pendingMenuCandidate;

    public static string CurrentMenu => currentMenu ?? string.Empty;

    public static string CurrentSection => currentSection ?? string.Empty;

    public static void Reset()
    {
        currentMenu = string.Empty;
        currentSection = string.Empty;
        pendingMenuCandidate = string.Empty;
    }

    public static void Observe(byte* label, bool selected, bool captureEnabled, string capturePlugin, string currentWindowName)
    {
        if (!selected || label == null || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        string? raw;
        try { raw = Marshal.PtrToStringUTF8((nint)label); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(raw)) return;

        var visible = VisibleLabel(raw);
        if (RsrProfile.IsNavigationMenu(visible))
        {
            pendingMenuCandidate = visible;
            return;
        }

        if (RsrProfile.MatchesWindow(currentWindowName) && RsrProfile.IsKnownSection(visible))
            currentSection = visible;
    }

    public static void ObserveAfterClick(string raw, bool captureEnabled, string capturePlugin)
    {
        if (string.IsNullOrWhiteSpace(raw) || !captureEnabled) return;
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;

        var visible = VisibleLabel(raw);
        if (!RsrProfile.IsNavigationMenu(visible)) return;

        if (!string.Equals(currentMenu, visible, StringComparison.Ordinal)) currentSection = string.Empty;
        pendingMenuCandidate = visible;
        currentMenu = visible;
    }

    public static void CommitPendingMenu(string capturePlugin)
    {
        if (!RsrProfile.MatchesPluginName(capturePlugin)) return;
        if (string.IsNullOrWhiteSpace(pendingMenuCandidate)) return;

        if (!string.Equals(currentMenu, pendingMenuCandidate, StringComparison.Ordinal)) currentSection = string.Empty;
        currentMenu = pendingMenuCandidate;
    }

    private static string VisibleLabel(string source)
    {
        var marker = source.IndexOf("##", StringComparison.Ordinal);
        return marker > 0 ? source[..marker] : source;
    }
}
