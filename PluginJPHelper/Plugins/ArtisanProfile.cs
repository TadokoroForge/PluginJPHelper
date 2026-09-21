namespace PluginJPHelper;

// Artisan 固有の処理。他のプラグインには影響しない。
// 元は Plugin.cs（動的表示の翻訳・ウィンドウキーワード補完）と
// Configuration.cs（既存設定へのキーワード移行）に散在していたものをここへ集約した。
internal static class ArtisanProfile
{
    public const string PluginName = "Artisan";

    // Artisan はメイン画面とは別に List Editor / Processing List というウィンドウ名を使う。
    // ウィンドウ名だけでは所有プラグインを判定できないため、これらも照合語に含める。
    private static readonly string[] RequiredWindowKeywords = ["Artisan", "List Editor", "Processing List"];

    public static bool MatchesPluginName(string? name)
        => string.Equals(name, PluginName, StringComparison.OrdinalIgnoreCase);

    // WindowKeyword へ不足分だけを補完する。ユーザーが設定した語は消さない。
    public static void EnsureWindowKeywords(List<string> keywords)
    {
        foreach (var required in RequiredWindowKeywords)
            if (!keywords.Contains(required, StringComparer.OrdinalIgnoreCase))
                keywords.Add(required);
    }

    // v0.3.1: Artisan の可変表示を安全に翻訳する。数値・アイテム名・ImGui ID は保持する。
    public static bool TryTranslateDynamic(string pluginName, string source, bool interactiveLabel, out string translated)
    {
        translated = string.Empty;
        if (!MatchesPluginName(pluginName)) return false;

        var idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        var idSuffix = idMarker >= 0 ? source[idMarker..] : string.Empty;
        string? result = null;

        const string listTime = "Approximate List Time: ";
        const string difficulty = "Difficulty: ";
        const string durability = " | Durability: ";
        const string quality = " | Quality: ";
        const string completedMinimum = "Craft completed and minimum quality required met in ";
        const string completedFullQuality = "Craft completed with full quality in ";
        const string currentProgress = "Current Item Progress: ";
        const string overallProgress = "Overall List Progress: ";
        const string remaining = "Approximate Remaining Duration: ";
        const string crafting = "Crafting: ";
        const string retainerItem = "Retainer Item: ";

        if (visible.StartsWith(listTime, StringComparison.Ordinal))
            result = "おおよそのリスト所要時間: " + visible[listTime.Length..];
        else if (visible.StartsWith(difficulty, StringComparison.Ordinal))
        {
            var durabilityPos = visible.IndexOf(durability, difficulty.Length, StringComparison.Ordinal);
            var qualityPos = durabilityPos >= 0 ? visible.IndexOf(quality, durabilityPos + durability.Length, StringComparison.Ordinal) : -1;
            if (durabilityPos > difficulty.Length && qualityPos > durabilityPos)
            {
                var difficultyValue = visible.Substring(difficulty.Length, durabilityPos - difficulty.Length);
                var durabilityValue = visible.Substring(durabilityPos + durability.Length, qualityPos - (durabilityPos + durability.Length));
                var qualityValue = visible[(qualityPos + quality.Length)..];
                result = $"難易度: {difficultyValue} | 耐久: {durabilityValue} | 品質: {qualityValue}";
            }
        }
        else if (visible.StartsWith(completedMinimum, StringComparison.Ordinal) && visible.EndsWith("s!", StringComparison.Ordinal))
        {
            var seconds = visible.Substring(completedMinimum.Length, visible.Length - completedMinimum.Length - 2);
            if (!string.IsNullOrWhiteSpace(seconds)) result = $"製作完了、必要最低品質を{seconds}秒で達成しました！";
        }
        else if (visible.StartsWith(completedFullQuality, StringComparison.Ordinal) && visible.EndsWith("s!", StringComparison.Ordinal))
        {
            var seconds = visible.Substring(completedFullQuality.Length, visible.Length - completedFullQuality.Length - 2);
            if (!string.IsNullOrWhiteSpace(seconds)) result = $"製作完了、最高品質を{seconds}秒で達成しました！";
        }
        else if (visible.StartsWith(currentProgress, StringComparison.Ordinal))
            result = "現在のアイテム進捗: " + visible[currentProgress.Length..];
        else if (visible.StartsWith(overallProgress, StringComparison.Ordinal))
            result = "リスト全体の進捗: " + visible[overallProgress.Length..];
        else if (visible.StartsWith(remaining, StringComparison.Ordinal))
            result = "おおよその残り時間: " + visible[remaining.Length..];
        else if (visible.StartsWith(crafting, StringComparison.Ordinal))
            result = "製作中: " + visible[crafting.Length..];
        else if (visible.StartsWith(retainerItem, StringComparison.Ordinal))
            result = "リテイナー所持品: " + visible[retainerItem.Length..];

        if (result == null) return false;
        translated = interactiveLabel ? result : result + idSuffix;
        return true;
    }
}
