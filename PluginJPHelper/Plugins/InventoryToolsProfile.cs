namespace PluginJPHelper.Plugins;

internal static class InventoryToolsProfile
{
    public const string PluginName = "InventoryTools";

    public const string DefaultWindowKeyword = "Configuration";

    private const string ConfigurationOwnerMenuLabel = "Wizard";

    private const string BundledCsvPrefix = "InventoryTools_JP_patch_v";
    private const string BundledCsvPattern = BundledCsvPrefix + "*.csv";

    public static bool IsConfigurationWindow(string? windowName)
        => string.Equals(windowName, DefaultWindowKeyword, StringComparison.Ordinal);

    public static bool IsConfigurationOwnerMenu(string? menuLabel)
        => string.Equals(menuLabel, ConfigurationOwnerMenuLabel, StringComparison.Ordinal);

    private static int GetPatchVersion(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(BundledCsvPrefix, StringComparison.OrdinalIgnoreCase)) return -1;

        return int.TryParse(name[BundledCsvPrefix.Length..], out var version) ? version : -1;
    }

    public static (string Path, int Version)? FindLatestBundledPatch(string assemblyDir)
    {
        var candidates = Directory
            .GetFiles(assemblyDir, BundledCsvPattern, SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Version: GetPatchVersion(path)))
            .Where(x => x.Version >= 0)
            .OrderByDescending(x => x.Version)
            .ToList();
        return candidates.Count == 0 ? null : candidates[0];
    }

    public static bool TryTranslateDynamic(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        if (!string.Equals(pluginName, PluginName, StringComparison.OrdinalIgnoreCase)) return false;

        const string sourcePrefix = "Can the item be sourced via ";
        const string sourceMiddle = "?\n\nIt includes these sources: ";
        const string usePrefix = "Can the item be used for ";
        const string useMiddle = "?\n\nIt includes these uses: ";
        const string nextAutosavePrefix = "Next Autosave: ";

        if (source.StartsWith(nextAutosavePrefix, StringComparison.Ordinal))
        {
            translated = "次回自動保存：" + source[nextAutosavePrefix.Length..];
            return true;
        }

        static string CategoryJa(string value) => value.Trim().ToLowerInvariant() switch
        {
            "botany" => "園芸",
            "crafting" => "製作",
            "deep dungeon" => "ディープダンジョン",
            "duties" => "コンテンツ",
            "field operation" => "特殊フィールド探索",
            "fishing" => "釣り",
            "gathering" => "採集",
            "gathering (ephemeral)" => "刻限の採集",
            "gathering (hidden)" => "未知の採集",
            "gathering (timed)" => "時間限定の採集",
            "mining" => "採掘",
            "venture" => "リテイナーベンチャー",
            "venture (exploration)" => "探索依頼",
            "leves" => "リーヴ",
            "shops" => "ショップ",
            "housing" => "ハウジング",
            "relic weapon" => "武器強化コンテンツ",
            "relic tool" => "道具強化コンテンツ",
            _ => value.Trim()
        };

        if (source.StartsWith(sourcePrefix, StringComparison.Ordinal))
        {
            var middle = source.IndexOf(sourceMiddle, sourcePrefix.Length, StringComparison.Ordinal);
            if (middle > sourcePrefix.Length)
            {
                var category = source.Substring(sourcePrefix.Length, middle - sourcePrefix.Length);
                var list = source[(middle + sourceMiddle.Length)..];
                translated = $"{CategoryJa(category)}で入手できるアイテムか？\n\n対象となる入手元：{list}";
                return true;
            }
        }

        if (source.StartsWith(usePrefix, StringComparison.Ordinal))
        {
            var middle = source.IndexOf(useMiddle, usePrefix.Length, StringComparison.Ordinal);
            if (middle > usePrefix.Length)
            {
                var category = source.Substring(usePrefix.Length, middle - usePrefix.Length);
                var list = source[(middle + useMiddle.Length)..];
                translated = $"{CategoryJa(category)}に使用するアイテムか？\n\n対象となる用途：{list}";
                return true;
            }
        }

        return false;
    }
}
