using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PluginJPHelper.Data;
using PluginJPHelper.Plugins;

namespace PluginJPHelper;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/pjph";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly ConcurrentDictionary<string, CapturedItem> backgroundCaptured = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConcurrentDictionary<string, CapturedItem>> pluginCaptured = new(StringComparer.Ordinal);
    private volatile bool baselineCaptureEnabled;
    private readonly Dictionary<string, string> editBuffers = new(StringComparer.Ordinal);
    // v0.0.66: 辞書画面の全カタログ再構築を毎フレーム行わない。
    private readonly Dictionary<string, KeyValuePair<string, string>[]> dictionaryCatalogCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> dictionaryMenuCountCache = new(StringComparer.Ordinal);
    private bool windowOpen = false;
    private volatile bool captureEnabled;
    private long translatedCount;
    private string filter = string.Empty;
    private bool showUntranslatedOnly;
    private int dictionarySortMode; // 0=原文, 1=画面内項目, 2=日本語訳, 3=未訳優先
    private bool dictionarySortAscending = true;
    private string customPluginName = string.Empty;
    private string customWindowKeyword = string.Empty;
    private string installedPluginSelection = string.Empty;
    private string csvStatus = string.Empty;
    private string hookStatus = "未初期化";
    private string selectedPlugin = "RSR";
    private string capturePlugin = "RSR";
    [ThreadStatic] private static bool drawingOwnUi;

    private Hook<TextUnformattedDelegate>? textHook;
    private Hook<TextWrappedDelegate>? textWrappedHook;
    private Hook<CheckboxDelegate>? checkboxHook;
    private Hook<ButtonDelegate>? buttonHook;
    private Hook<SelectableDelegate>? selectableHook;
    private Hook<SelectablePtrDelegate>? selectablePtrHook;
    private Hook<ComboStrArrDelegate>? comboStrArrHook;
    private Hook<ComboStrDelegate>? comboStrHook;
    private Hook<ComboFnStrPtrDelegate>? comboFnStrPtrHook;
    private Hook<BeginComboDelegate>? beginComboHook;
    private Hook<EndComboDelegate>? endComboHook;
    private Hook<SeparatorTextDelegate>? separatorTextHook;
    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;
    private Hook<RadioButtonBoolDelegate>? radioButtonBoolHook;
    private Hook<RadioButtonIntPtrDelegate>? radioButtonIntPtrHook;
    private Hook<TreeNodeStrDelegate>? treeNodeStrHook;
    private Hook<TreeNodeExStrDelegate>? treeNodeExStrHook;
    private Hook<CollapsingHeaderTreeNodeFlagsDelegate>? collapsingHeaderTreeNodeFlagsHook;
    private Hook<CollapsingHeaderBoolPtrDelegate>? collapsingHeaderBoolPtrHook;
    private Hook<BulletTextDelegate>? bulletTextHook;
    private Hook<RenderTextDelegate>? renderTextHook;
    private Hook<RenderTextWrappedDelegate>? renderTextWrappedHook;
    private Hook<RenderTextClippedDelegate>? renderTextClippedHook;
    private Hook<DrawListAddTextVec2Delegate>? drawListAddTextVec2Hook;
    private Hook<BeginTabItemDelegate>? beginTabItemHook;
    private Hook<MenuItemBoolDelegate>? menuItemBoolHook;
    private Hook<MenuItemBoolPtrDelegate>? menuItemBoolPtrHook;
    private Hook<BeginMenuDelegate>? beginMenuHook;
    [ThreadStatic] private static Stack<string>? windowStack;
    [ThreadStatic] private static int comboOpenDepth;
    private long comboHookCalls;
    private long comboTranslatedItems;
    private long comboStrCalls;
    private long comboFnCalls;
    private long beginComboCalls;
    private long comboSelectableCalls;
    private readonly ConcurrentDictionary<string, int> seenWindows = new(StringComparer.Ordinal);
    private static Stack<string>? windowOwnerStack;
    private string lastExplicitWindowOwner = string.Empty;
    private long lastExplicitWindowOwnerTick;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextUnformattedDelegate(byte* text, byte* textEnd);
    // Dalamud Bindings (2025+) の ImGui.TextWrapped(string) は安全化された cimgui 経路を使用する。
    // RSR のチェック項目ラベルは TextUnformatted ではなくこの経路に来る。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextWrappedDelegate(byte* text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CheckboxDelegate(byte* label, byte* value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ButtonDelegate(byte* label, Vector2 size);
    // cimgui: bool igSelectable_Bool(const char* label, bool selected, ImGuiSelectableFlags flags, const ImVec2 size)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SelectableDelegate(byte* label, byte selected, int flags, Vector2 size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SelectablePtrDelegate(byte* label, byte* selected, int flags, Vector2 size);
    // cimgui: bool igCombo_Str_arr(const char* label, int* current_item, const char* const items[], int items_count, int popup_max_height_in_items)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboStrArrDelegate(byte* label, int* currentItem, byte** items, int itemsCount, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboStrDelegate(byte* label, int* currentItem, byte* itemsSeparatedByZeros, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ComboFnStrPtrDelegate(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginComboDelegate(byte* label, byte* previewValue, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndComboDelegate();
    // cimgui: bool igBeginTabItem(const char* label, bool* p_open, ImGuiTabItemFlags flags)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginTabItemDelegate(byte* label, byte* pOpen, int flags);
    // cimgui: bool igMenuItem_Bool(const char* label, const char* shortcut, bool selected, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MenuItemBoolDelegate(byte* label, byte* shortcut, byte selected, byte enabled);
    // cimgui: bool igMenuItem_BoolPtr(const char* label, const char* shortcut, bool* p_selected, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte MenuItemBoolPtrDelegate(byte* label, byte* shortcut, byte* pSelected, byte enabled);
    // cimgui: bool igBeginMenu(const char* label, bool enabled)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginMenuDelegate(byte* label, byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SeparatorTextDelegate(byte* label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeginDelegate(byte* name, byte* pOpen, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EndDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte RadioButtonBoolDelegate(byte* label, byte active);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte RadioButtonIntPtrDelegate(byte* label, int* v, int vButton);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte TreeNodeStrDelegate(byte* label);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte TreeNodeExStrDelegate(byte* label, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CollapsingHeaderTreeNodeFlagsDelegate(byte* label, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CollapsingHeaderBoolPtrDelegate(byte* label, byte* pVisible, int flags);
    // Dalamud.Bindings.ImGui の ImGui.BulletText(string) が利用する cimgui 経路。
    // RSRのツールチップ説明文はこの経路で描画される。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BulletTextDelegate(byte* text);

    // Dalamud.Bindings.ImGui の TextWrapped / TreeNode / BulletText は、API15では
    // cimgui の公開 TextWrapped/TreeNode/BulletText を通らず ImGuiP.RenderText 系へ直接描画する。
    // 表示段階だけを差し替えるため、Widget ID や設定値には一切触れない。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextDelegate(Vector2 pos, byte* text, byte* textEnd, byte hideTextAfterHash);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextWrappedDelegate(Vector2 pos, byte* text, byte* textEnd, float wrapWidth);

    // v0.0.72: ImGui.LabelText() の表示値は RenderText/RenderTextWrapped ではなく
    // RenderTextClipped を通る。Allagan Tools の設定ラベルはこの経路を多用する。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImRect
    {
        public Vector2 Min;
        public Vector2 Max;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderTextClippedDelegate(
        Vector2 posMin, Vector2 posMax, byte* text, byte* textEnd,
        Vector2* textSizeIfKnown, Vector2 align, NativeImRect* clipRect);

    private delegate void DrawListAddTextVec2Delegate(void* drawList, Vector2 pos, uint col, byte* textBegin, byte* textEnd);

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IGameInteropProvider interop, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.interop = interop;
        this.log = log;
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.EnsurePlugins();
        EnsureCaptureDictionaries();
        // v0.0.23: 完全リセット版。更新時に一度だけ旧データを全消去する。
        // バンドル済み旧標準辞書も CleanSlateMode で無効化し、今回の再取得結果だけを新基準にする。
        if (config.DataResetVersion < 23)
        {
            foreach (var state in config.Plugins.Values)
            {
                state.UserOverrides.Clear();
                state.Locations.Clear();
                state.Enabled = false;
            }
            config.Plugins["RSR"].Enabled = true;
            config.CaptureSchemaVersion = 3;
            config.CleanSlateMode = true;
            config.DataResetVersion = 24;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 24)
        {
            // v0.0.25: 既存のv0.0.23所属情報は保持。再取得は要求しない。
            config.DataResetVersion = 24;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 26)
        {
            // v0.0.26: バンドル済みRSR標準辞書を復元。既存ユーザー訳・所属情報は保持する。
            config.CleanSlateMode = false;
            config.Plugins["RSR"].Enabled = true;
            config.DataResetVersion = 26;
            pluginInterface.SavePluginConfig(config);
        }
        if (config.DataResetVersion < 27)
        {
            // v0.0.28: v0.0.23で取得済みの所属を標準辞書へ復元。
            // ユーザー翻訳は保持し、所属だけをクリーンに入れ直す。
            var rsr = config.Plugins["RSR"];
            rsr.Locations.Clear();
            foreach (var kv in RsrBundledLocations.ByKey)
                rsr.Locations[kv.Key] = new() { Menu = kv.Value.Menu, Section = kv.Value.Section };
            config.CleanSlateMode = false;
            rsr.Enabled = true;
            config.DataResetVersion = 27;
            pluginInterface.SavePluginConfig(config);
            _ = ExportDictionaryCsv("RSR");
        }
        if (config.DataResetVersion < 42)
        {
            // v0.0.66: 翻訳済み/未翻訳を問わず、取得済みの所属情報を差分で復元する。
            // 既存のユーザー訳は保持し、翻訳が空欄の項目も辞書一覧へ出るよう Locations を追加する。
            var rsr = config.Plugins["RSR"];
            foreach (var kv in RsrBundledLocations.ByKey)
                rsr.Locations[kv.Key] = new() { Menu = kv.Value.Menu, Section = kv.Value.Section };
            foreach (var kv in RsrCapturedLocationsV42.ByKey)
                rsr.Locations[kv.Key] = new() { Menu = kv.Value.Menu, Section = kv.Value.Section };
            config.CleanSlateMode = false;
            rsr.Enabled = true;
            config.DataResetVersion = 42;
            pluginInterface.SavePluginConfig(config);
            _ = ExportDictionaryCsv("RSR");
        }

        // v0.0.73: InventoryTools の同梱差分CSVから最大バージョンを起動時に1回だけ確認する。
        // 毎フレーム検索はせず、同じCSVが既に読込済みなら再読込もしない。
        _ = EnsureLatestBundledInventoryToolsCsv();

        EnsureInitialRsrCsv();

        commandManager.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Plugin JP Helper を開きます。" });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenUi;
        InstallHooks();
    }

    private void InstallHooks()
    {
        var installed = new List<string>();
        var failed = new List<string>();
        try { textHook = interop.HookFromSymbol<TextUnformattedDelegate>("cimgui.dll", "igTextUnformatted", TextUnformattedDetour); textHook.Enable(); installed.Add("Text"); }
        catch (Exception ex) { failed.Add("Text"); log.Warning(ex, "[PluginJPHelper] igTextUnformatted hook failed"); }
        try { textWrappedHook = interop.HookFromSymbol<TextWrappedDelegate>("cimgui.dll", "igTextWrapped", TextWrappedDetour); textWrappedHook.Enable(); installed.Add("TextWrapped"); }
        catch (Exception ex) { failed.Add("TextWrapped"); log.Warning(ex, "[PluginJPHelper] igTextWrapped hook failed"); }
        try { checkboxHook = interop.HookFromSymbol<CheckboxDelegate>("cimgui.dll", "igCheckbox", CheckboxDetour); checkboxHook.Enable(); installed.Add("Checkbox"); }
        catch (Exception ex) { failed.Add("Checkbox"); log.Warning(ex, "[PluginJPHelper] igCheckbox hook failed"); }
        try { buttonHook = interop.HookFromSymbol<ButtonDelegate>("cimgui.dll", "igButton", ButtonDetour); buttonHook.Enable(); installed.Add("Button"); }
        catch (Exception ex) { failed.Add("Button"); log.Warning(ex, "[PluginJPHelper] igButton hook failed"); }
        try { selectableHook = interop.HookFromSymbol<SelectableDelegate>("cimgui.dll", "igSelectable_Bool", SelectableDetour); selectableHook.Enable(); installed.Add("Selectable"); }
        catch (Exception ex) { failed.Add("Selectable"); log.Warning(ex, "[PluginJPHelper] igSelectable_Bool hook failed"); }
        try { selectablePtrHook = interop.HookFromSymbol<SelectablePtrDelegate>("cimgui.dll", "igSelectable_BoolPtr", SelectablePtrDetour); selectablePtrHook.Enable(); installed.Add("SelectablePtr"); }
        catch (Exception ex) { failed.Add("SelectablePtr"); log.Warning(ex, "[PluginJPHelper] igSelectable_BoolPtr hook failed"); }
        try { comboStrArrHook = interop.HookFromSymbol<ComboStrArrDelegate>("cimgui.dll", "igCombo_Str_arr", ComboStrArrDetour); comboStrArrHook.Enable(); installed.Add("ComboStrArr"); }
        catch (Exception ex) { failed.Add("ComboStrArr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str_arr hook failed"); }
        try { comboStrHook = interop.HookFromSymbol<ComboStrDelegate>("cimgui.dll", "igCombo_Str", ComboStrDetour); comboStrHook.Enable(); installed.Add("ComboStr"); }
        catch (Exception ex) { failed.Add("ComboStr"); log.Warning(ex, "[PluginJPHelper] igCombo_Str hook failed"); }
        try { comboFnStrPtrHook = interop.HookFromSymbol<ComboFnStrPtrDelegate>("cimgui.dll", "igCombo_FnStrPtr", ComboFnStrPtrDetour); comboFnStrPtrHook.Enable(); installed.Add("ComboFn"); }
        catch (Exception ex) { failed.Add("ComboFn"); log.Warning(ex, "[PluginJPHelper] igCombo_FnStrPtr hook failed"); }
        try { beginComboHook = interop.HookFromSymbol<BeginComboDelegate>("cimgui.dll", "igBeginCombo", BeginComboDetour); beginComboHook.Enable(); installed.Add("BeginCombo"); }
        catch (Exception ex) { failed.Add("BeginCombo"); log.Warning(ex, "[PluginJPHelper] igBeginCombo hook failed"); }
        try { endComboHook = interop.HookFromSymbol<EndComboDelegate>("cimgui.dll", "igEndCombo", EndComboDetour); endComboHook.Enable(); installed.Add("EndCombo"); }
        catch (Exception ex) { failed.Add("EndCombo"); log.Warning(ex, "[PluginJPHelper] igEndCombo hook failed"); }
        try { beginTabItemHook = interop.HookFromSymbol<BeginTabItemDelegate>("cimgui.dll", "igBeginTabItem", BeginTabItemDetour); beginTabItemHook.Enable(); installed.Add("BeginTabItem"); }
        catch (Exception ex) { failed.Add("BeginTabItem"); log.Warning(ex, "[PluginJPHelper] igBeginTabItem hook failed"); }
        try { menuItemBoolHook = interop.HookFromSymbol<MenuItemBoolDelegate>("cimgui.dll", "igMenuItem_Bool", MenuItemBoolDetour); menuItemBoolHook.Enable(); installed.Add("MenuItem"); }
        catch (Exception ex) { failed.Add("MenuItem"); log.Warning(ex, "[PluginJPHelper] igMenuItem_Bool hook failed"); }
        try { menuItemBoolPtrHook = interop.HookFromSymbol<MenuItemBoolPtrDelegate>("cimgui.dll", "igMenuItem_BoolPtr", MenuItemBoolPtrDetour); menuItemBoolPtrHook.Enable(); installed.Add("MenuItemPtr"); }
        catch (Exception ex) { failed.Add("MenuItemPtr"); log.Warning(ex, "[PluginJPHelper] igMenuItem_BoolPtr hook failed"); }
        try { beginMenuHook = interop.HookFromSymbol<BeginMenuDelegate>("cimgui.dll", "igBeginMenu", BeginMenuDetour); beginMenuHook.Enable(); installed.Add("BeginMenu"); }
        catch (Exception ex) { failed.Add("BeginMenu"); log.Warning(ex, "[PluginJPHelper] igBeginMenu hook failed"); }
        try { separatorTextHook = interop.HookFromSymbol<SeparatorTextDelegate>("cimgui.dll", "igSeparatorText", SeparatorTextDetour); separatorTextHook.Enable(); installed.Add("SeparatorText"); }
        catch (Exception ex) { failed.Add("SeparatorText"); log.Warning(ex, "[PluginJPHelper] igSeparatorText hook failed"); }
        try { beginHook = interop.HookFromSymbol<BeginDelegate>("cimgui.dll", "igBegin", BeginDetour); beginHook.Enable(); installed.Add("Begin"); }
        catch (Exception ex) { failed.Add("Begin"); log.Warning(ex, "[PluginJPHelper] igBegin hook failed"); }
        try { endHook = interop.HookFromSymbol<EndDelegate>("cimgui.dll", "igEnd", EndDetour); endHook.Enable(); installed.Add("End"); }
        catch (Exception ex) { failed.Add("End"); log.Warning(ex, "[PluginJPHelper] igEnd hook failed"); }
        try { radioButtonBoolHook = interop.HookFromSymbol<RadioButtonBoolDelegate>("cimgui.dll", "igRadioButton_Bool", RadioButtonBoolDetour); radioButtonBoolHook.Enable(); installed.Add("RadioButtonBool"); }
        catch (Exception ex) { failed.Add("RadioButtonBool"); log.Warning(ex, "[PluginJPHelper] igRadioButton_Bool hook failed"); }
        try { radioButtonIntPtrHook = interop.HookFromSymbol<RadioButtonIntPtrDelegate>("cimgui.dll", "igRadioButton_IntPtr", RadioButtonIntPtrDetour); radioButtonIntPtrHook.Enable(); installed.Add("RadioButtonIntPtr"); }
        catch (Exception ex) { failed.Add("RadioButtonIntPtr"); log.Warning(ex, "[PluginJPHelper] igRadioButton_IntPtr hook failed"); }
        try { treeNodeStrHook = interop.HookFromSymbol<TreeNodeStrDelegate>("cimgui.dll", "igTreeNode_Str", TreeNodeStrDetour); treeNodeStrHook.Enable(); installed.Add("TreeNode"); }
        catch (Exception ex) { failed.Add("TreeNode"); log.Warning(ex, "[PluginJPHelper] igTreeNode_Str hook failed"); }
        try { treeNodeExStrHook = interop.HookFromSymbol<TreeNodeExStrDelegate>("cimgui.dll", "igTreeNodeEx_Str", TreeNodeExStrDetour); treeNodeExStrHook.Enable(); installed.Add("TreeNodeEx"); }
        catch (Exception ex) { failed.Add("TreeNodeEx"); log.Warning(ex, "[PluginJPHelper] igTreeNodeEx_Str hook failed"); }
        try { collapsingHeaderTreeNodeFlagsHook = interop.HookFromSymbol<CollapsingHeaderTreeNodeFlagsDelegate>("cimgui.dll", "igCollapsingHeader_TreeNodeFlags", CollapsingHeaderTreeNodeFlagsDetour); collapsingHeaderTreeNodeFlagsHook.Enable(); installed.Add("CollapsingHeader"); }
        catch (Exception ex) { failed.Add("CollapsingHeader"); log.Warning(ex, "[PluginJPHelper] igCollapsingHeader_TreeNodeFlags hook failed"); }
        try { collapsingHeaderBoolPtrHook = interop.HookFromSymbol<CollapsingHeaderBoolPtrDelegate>("cimgui.dll", "igCollapsingHeader_BoolPtr", CollapsingHeaderBoolPtrDetour); collapsingHeaderBoolPtrHook.Enable(); installed.Add("CollapsingHeaderPtr"); }
        catch (Exception ex) { failed.Add("CollapsingHeaderPtr"); log.Warning(ex, "[PluginJPHelper] igCollapsingHeader_BoolPtr hook failed"); }
        try { bulletTextHook = interop.HookFromSymbol<BulletTextDelegate>("cimgui.dll", "igBulletText", BulletTextDetour); bulletTextHook.Enable(); installed.Add("BulletText"); }
        catch (Exception ex) { failed.Add("BulletText"); log.Warning(ex, "[PluginJPHelper] igBulletText hook failed"); }
        try { renderTextHook = interop.HookFromSymbol<RenderTextDelegate>("cimgui.dll", "igRenderText", RenderTextDetour); renderTextHook.Enable(); installed.Add("RenderText"); }
        catch (Exception ex) { failed.Add("RenderText"); log.Warning(ex, "[PluginJPHelper] igRenderText hook failed"); }
        try { renderTextWrappedHook = interop.HookFromSymbol<RenderTextWrappedDelegate>("cimgui.dll", "igRenderTextWrapped", RenderTextWrappedDetour); renderTextWrappedHook.Enable(); installed.Add("RenderTextWrapped"); }
        catch (Exception ex) { failed.Add("RenderTextWrapped"); log.Warning(ex, "[PluginJPHelper] igRenderTextWrapped hook failed"); }
        try { renderTextClippedHook = interop.HookFromSymbol<RenderTextClippedDelegate>("cimgui.dll", "igRenderTextClipped", RenderTextClippedDetour); renderTextClippedHook.Enable(); installed.Add("RenderTextClipped"); }
        catch (Exception ex) { failed.Add("RenderTextClipped"); log.Warning(ex, "[PluginJPHelper] igRenderTextClipped hook failed"); }
        try { drawListAddTextVec2Hook = interop.HookFromSymbol<DrawListAddTextVec2Delegate>("cimgui.dll", "ImDrawList_AddText_Vec2", DrawListAddTextVec2Detour); drawListAddTextVec2Hook.Enable(); installed.Add("DrawListAddText"); }
        catch (Exception ex) { failed.Add("DrawListAddText"); log.Warning(ex, "[PluginJPHelper] ImDrawList_AddText_Vec2 hook failed"); }
        hookStatus = installed.Count == 0 ? "フック失敗（Dalamudログを確認）" : $"有効: {string.Join(", ", installed)}" + (failed.Count > 0 ? $" / 失敗: {string.Join(", ", failed)}" : string.Empty);
    }

    private void TextUnformattedDetour(byte* text, byte* textEnd)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "Text"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(text, textEnd, false, out var translated)) { CallTextOriginal(translated); Interlocked.Increment(ref translatedCount); return; }
        textHook!.Original(text, textEnd);
    }

    private void TextWrappedDetour(byte* text)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, null, "TextWrapped"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(text, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igTextWrapped は printf 形式のAPI。Dalamud の安全ラッパーと同様、% はリテラル扱いにする。
            var safe = translated.Replace("%", "%%", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(safe + "\0");
            fixed (byte* p = bytes) { textWrappedHook!.Original(p); return; }
        }
        textWrappedHook!.Original(text);
    }

    private byte CheckboxDetour(byte* label, byte* value)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Checkbox"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated)) { Interlocked.Increment(ref translatedCount); return CallCheckboxOriginal(MakeDisplayOnlyInteractiveLabel(label, translated), value); }
        return checkboxHook!.Original(label, value);
    }

    private byte ButtonDetour(byte* label, Vector2 size)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Button"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated)) { Interlocked.Increment(ref translatedCount); return CallButtonOriginal(MakeDisplayOnlyInteractiveLabel(label, translated), size); }
        return buttonHook!.Original(label, size);
    }


    private byte SelectableDetour(byte* label, byte selected, int flags, Vector2 size)
    {
        string raw = string.Empty;
        try { if (label != null) raw = Marshal.PtrToStringUTF8((nint)label) ?? string.Empty; } catch { }
        try { if (!drawingOwnUi) UpdateRsrNavigationContext(label, selected != 0); } catch { }
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "Selectable"); } catch { }

        byte result;
        if (!drawingOwnUi && TryTranslateSelectableLabel(label, out var displayOnly))
        {
            Interlocked.Increment(ref translatedCount);
            if (comboOpenDepth > 0) Interlocked.Increment(ref comboTranslatedItems);
            var bytes = Encoding.UTF8.GetBytes(displayOnly + "\0");
            fixed (byte* ptr = bytes) result = selectableHook!.Original(ptr, selected, flags, size);
        }
        else
        {
            result = selectableHook!.Original(label, selected, flags, size);
        }

        if (!drawingOwnUi && comboOpenDepth > 0) Interlocked.Increment(ref comboSelectableCalls);
        try { if (!drawingOwnUi && result != 0) UpdateRsrNavigationContextAfterClick(raw); } catch { }
        return result;
    }

    private byte SelectablePtrDetour(byte* label, byte* selected, int flags, Vector2 size)
    {
        string raw = string.Empty;
        try { if (label != null) raw = Marshal.PtrToStringUTF8((nint)label) ?? string.Empty; } catch { }
        try { if (!drawingOwnUi) UpdateRsrNavigationContext(label, selected != null && *selected != 0); } catch { }
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "SelectablePtr"); } catch { }

        byte result;
        if (!drawingOwnUi && TryTranslateSelectableLabel(label, out var displayOnly))
        {
            Interlocked.Increment(ref translatedCount);
            if (comboOpenDepth > 0) Interlocked.Increment(ref comboTranslatedItems);
            var bytes = Encoding.UTF8.GetBytes(displayOnly + "\0");
            fixed (byte* ptr = bytes) result = selectablePtrHook!.Original(ptr, selected, flags, size);
        }
        else
        {
            result = selectablePtrHook!.Original(label, selected, flags, size);
        }

        if (!drawingOwnUi && comboOpenDepth > 0) Interlocked.Increment(ref comboSelectableCalls);
        try { if (!drawingOwnUi && result != 0) UpdateRsrNavigationContextAfterClick(raw); } catch { }
        return result;
    }

    private bool TryTranslateSelectableLabel(byte* label, out string displayOnly)
    {
        displayOnly = string.Empty;
        if (label == null) return false;
        string? raw;
        try { raw = Marshal.PtrToStringUTF8((nint)label); }
        catch { return false; }
        if (string.IsNullOrEmpty(raw)) return false;

        // 通常Selectableは完全一致だけ。Combo内だけは表示部(##/###より前)で辞書照合し、
        // Widget IDは元ラベルをそのまま保持する。
        if (TryTranslate(raw, out var exact))
        {
            displayOnly = MakeDisplayOnlyInteractiveLabel(label, exact);
            return true;
        }
        // v0.0.66: Selectableも動的表示文字列の部分翻訳を許可する。
        // 内部IDは MakeDisplayOnlyInteractiveLabel() で元ラベルを保持する。
        if (TryTranslatePointer(label, null, true, out var partial))
        {
            displayOnly = MakeDisplayOnlyInteractiveLabel(label, partial);
            return true;
        }

        if (comboOpenDepth <= 0) return false;

        var visible = VisibleLabel(raw);
        if (string.Equals(visible, raw, StringComparison.Ordinal) || !TryTranslate(visible, out var ja)) return false;
        displayOnly = MakeDisplayOnlyInteractiveLabel(label, ja);
        return true;
    }

    private byte ComboStrArrDetour(byte* label, int* currentItem, byte** items, int itemsCount, int popupMaxHeightInItems)
    {
        // RSRは TargetingType 等のプルダウンを ImGui.Combo(string[]) で描画する。
        // この経路では内部で作られる Selectable は外側のSelectableフックを通らないため、
        // 配列自体を「日本語表示###英語ID」に差し替える。currentItem(選択index)は一切変更しない。
        if (drawingOwnUi || items == null || itemsCount <= 0 || itemsCount > 512)
            return comboStrArrHook!.Original(label, currentItem, items, itemsCount, popupMaxHeightInItems);

        Interlocked.Increment(ref comboHookCalls);
        var allocated = new List<nint>();
        try
        {
            byte** translatedItems = stackalloc byte*[itemsCount];
            var changed = false;
            for (var i = 0; i < itemsCount; i++)
            {
                translatedItems[i] = items[i];
                if (items[i] == null) continue;
                var source = Marshal.PtrToStringUTF8((nint)items[i]);
                if (string.IsNullOrEmpty(source)) continue;
                if (!TryTranslate(source, out var translated)) continue;

                // Comboは選択値をindexで保持しているため、候補文字列を日本語へ置換しても
                // RSR内部の値・保存値は変化しない。候補側に###IDを付けるとpreview表示で
                // そのまま見える経路があるため、ここでは表示文字列だけを渡す。
                var bytes = Encoding.UTF8.GetBytes(translated + "\0");
                var mem = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, mem, bytes.Length);
                allocated.Add(mem);
                translatedItems[i] = (byte*)mem;
                changed = true;
                Interlocked.Increment(ref comboTranslatedItems);
            }

            return changed
                ? comboStrArrHook!.Original(label, currentItem, translatedItems, itemsCount, popupMaxHeightInItems)
                : comboStrArrHook!.Original(label, currentItem, items, itemsCount, popupMaxHeightInItems);
        }
        finally
        {
            foreach (var mem in allocated) Marshal.FreeHGlobal(mem);
        }
    }


    private byte ComboStrDetour(byte* label, int* currentItem, byte* itemsSeparatedByZeros, int popupMaxHeightInItems)
    {
        if (drawingOwnUi || itemsSeparatedByZeros == null)
            return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

        Interlocked.Increment(ref comboStrCalls);
        try
        {
            // NUL区切り文字列を最大64KiBまで安全に読み、最後の\0\0で終了する。
            var items = new List<string>();
            var p = itemsSeparatedByZeros;
            var total = 0;
            while (total < 65536)
            {
                var len = 0;
                while (total + len < 65536 && p[len] != 0) len++;
                if (len == 0) break;
                var src = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, len));
                items.Add(src);
                p += len + 1;
                total += len + 1;
            }
            if (items.Count == 0)
                return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

            var changed = false;
            var translated = new List<string>(items.Count);
            foreach (var src in items)
            {
                if (TryTranslate(src, out var ja))
                {
                    translated.Add(ja);
                    changed = true;
                    Interlocked.Increment(ref comboTranslatedItems);
                }
                else translated.Add(src);
            }
            if (!changed)
                return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);

            using var ms = new MemoryStream();
            foreach (var item in translated)
            {
                var b = Encoding.UTF8.GetBytes(item);
                ms.Write(b); ms.WriteByte(0);
            }
            ms.WriteByte(0);
            var packed = ms.ToArray();
            fixed (byte* pp = packed)
                return comboStrHook!.Original(label, currentItem, pp, popupMaxHeightInItems);
        }
        catch
        {
            return comboStrHook!.Original(label, currentItem, itemsSeparatedByZeros, popupMaxHeightInItems);
        }
    }

    private byte ComboFnStrPtrDetour(byte* label, int* currentItem, nint getter, void* userData, int itemsCount, int popupMaxHeightInItems)
    {
        // まず実機でこの経路を使っているかだけ確認する。getter差し替えは誤動作リスクがあるためまだ行わない。
        Interlocked.Increment(ref comboFnCalls);
        return comboFnStrPtrHook!.Original(label, currentItem, getter, userData, itemsCount, popupMaxHeightInItems);
    }

    private byte BeginComboDetour(byte* label, byte* previewValue, int flags)
    {
        Interlocked.Increment(ref beginComboCalls);
        byte result;
        if (drawingOwnUi || previewValue == null)
        {
            result = beginComboHook!.Original(label, previewValue, flags);
        }
        else
        {
            try
            {
                var src = Marshal.PtrToStringUTF8((nint)previewValue);
                if (!string.IsNullOrEmpty(src) && TryTranslate(src, out var ja))
                {
                    var bytes = Encoding.UTF8.GetBytes(ja + "\0");
                    fixed (byte* p = bytes) result = beginComboHook!.Original(label, p, flags);
                }
                else result = beginComboHook!.Original(label, previewValue, flags);
            }
            catch { result = beginComboHook!.Original(label, previewValue, flags); }
        }

        if (!drawingOwnUi && result != 0) comboOpenDepth++;
        return result;
    }

    private void EndComboDetour()
    {
        try { endComboHook!.Original(); }
        finally
        {
            if (!drawingOwnUi && comboOpenDepth > 0) comboOpenDepth--;
        }
    }

    private byte RadioButtonBoolDetour(byte* label, byte active)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "RadioButtonBool"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return radioButtonBoolHook!.Original(p, active);
        }
        return radioButtonBoolHook!.Original(label, active);
    }

    private byte RadioButtonIntPtrDetour(byte* label, int* v, int vButton)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "RadioButtonIntPtr"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return radioButtonIntPtrHook!.Original(p, v, vButton);
        }
        return radioButtonIntPtrHook!.Original(label, v, vButton);
    }

    private byte TreeNodeStrDetour(byte* label)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TreeNode"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return treeNodeStrHook!.Original(p);
        }
        return treeNodeStrHook!.Original(label);
    }

    private byte TreeNodeExStrDetour(byte* label, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TreeNodeEx"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return treeNodeExStrHook!.Original(p, flags);
        }
        return treeNodeExStrHook!.Original(label, flags);
    }


    private byte CollapsingHeaderTreeNodeFlagsDetour(byte* label, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "CollapsingHeader"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
                return collapsingHeaderTreeNodeFlagsHook!.Original(p, flags);
        }

        return collapsingHeaderTreeNodeFlagsHook!.Original(label, flags);
    }

    private byte CollapsingHeaderBoolPtrDetour(byte* label, byte* pVisible, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "CollapsingHeaderPtr"); } catch { }

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes)
                return collapsingHeaderBoolPtrHook!.Original(p, pVisible, flags);
        }

        return collapsingHeaderBoolPtrHook!.Original(label, pVisible, flags);
    }

    private void BulletTextDetour(byte* text)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, null, "Tooltip"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(text, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            // igBulletText は printf形式なので、日本語訳中の % はリテラルとしてエスケープする。
            var safe = translated.Replace("%", "%%", StringComparison.Ordinal);
            var bytes = Encoding.UTF8.GetBytes(safe + "\0");
            fixed (byte* p = bytes) { bulletTextHook!.Original(p); return; }
        }
        bulletTextHook!.Original(text);
    }

    private bool TryGetTranslationForPlugin(string pluginName, string source, out string translated)
    {
        translated = string.Empty;
        if (!config.Plugins.TryGetValue(pluginName, out var state) || !state.Enabled) return false;
        if (state.UserOverrides.TryGetValue(source, out translated!) && !string.IsNullOrWhiteSpace(translated)) return true;
        return GetActiveStandardDictionary(pluginName).TryGetValue(source, out translated!);
    }

    private static bool AllowsPartialTranslation(string pluginName)
        => DalamudActProfile.MatchesPluginName(pluginName);


    // v0.0.66: RenderText系は「現在のウィンドウがどの対象プラグインか」を確定してから、
    // そのプラグインの辞書だけを参照する。設定値・ImGui ID・入力状態には触れない。
    private bool TryTranslateRenderPointerForCurrentWindow(byte* begin, byte* end, out string translated, out string pluginName)
    {
        translated = string.Empty;
        pluginName = string.Empty;
        if (begin == null) return false;

        var currentWindow = CurrentWindowName;
        if (string.IsNullOrWhiteSpace(currentWindow)) return false;

        foreach (var (name, state) in config.Plugins)
        {
            if (!state.Enabled || !(string.Equals(CurrentWindowOwner, name, StringComparison.Ordinal) || IsTargetWindow(name, currentWindow))) continue;
            pluginName = name;
            break;
        }
        if (string.IsNullOrEmpty(pluginName)) return false;

        string? source;
        if (end != null && end >= begin)
        {
            var len = end - begin;
            if (len <= 0 || len > 4096) return false;
            source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len));
        }
        else source = Marshal.PtrToStringUTF8((nint)begin);

        if (string.IsNullOrEmpty(source)) return false;

        // まず従来どおり完全一致。
        if (TryGetTranslationForPlugin(pluginName, source, out translated)) return true;

        if (InventoryToolsProfile.TryTranslateDynamic(pluginName, source, out translated)) return true;
        if (ArtisanProfile.TryTranslateDynamic(pluginName, source, false, out translated)) return true;

        // v0.0.66: 部分一致はDalamudACTの動的ラベルだけに限定する。
        // ICEなど通常のプラグインは完全一致だけで処理し、辞書全件走査を行わない。
        if (!AllowsPartialTranslation(pluginName)) return false;

        // v0.0.66: 動的な表示文字列の中に辞書原文が含まれる場合だけ、
        // 「表示部分」に限定して部分置換する。
        // 例: "01/21 白魔法师" -> "01/21 白魔道士"
        // ImGui の ## / ### 以降は Widget ID / 内部ID なので絶対に変更しない。
        var idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        var idSuffix = idMarker >= 0 ? source[idMarker..] : string.Empty;
        if (string.IsNullOrEmpty(visible)) return false;

        if (!config.Plugins.TryGetValue(pluginName, out var pluginState)) return false;

        var candidates = GetDictionaryCatalog(pluginName)
            .Select(kv => (Source: kv.Key, Ja: pluginState.UserOverrides.TryGetValue(kv.Key, out var userJa) && !string.IsNullOrWhiteSpace(userJa) ? userJa : kv.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("##", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;
        var replaced = visible;
        var changed = false;
        foreach (var x in candidates)
        {
            var next = replaced.Replace(x.Source, x.Ja, StringComparison.Ordinal);
            if (!string.Equals(next, replaced, StringComparison.Ordinal)) changed = true;
            replaced = next;
        }
        if (!changed) return false;

        translated = replaced + idSuffix;
        return true;
    }

    private void DrawListAddTextVec2Detour(void* drawList, Vector2 pos, uint col, byte* textBegin, byte* textEnd)
    {
        // v0.0.66:
        // DalamudACT の PartyMonitorWindow は ImGui.Text 系ではなく
        // ImDrawList.AddText() で短縮ジョブ名を直接描画する。
        // この経路は通常の Text/RenderText フックを通らないため、DalamudACT の
        // ウィンドウ内だけを対象に最小限の完全一致置換を行う。
        if (drawingOwnUi || textBegin == null || !IsCurrentWindowOwnedBy(DalamudActProfile.PluginName))
        {
            drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
            return;
        }

        string? source = null;
        try
        {
            if (textEnd != null && textEnd >= textBegin)
            {
                var len = textEnd - textBegin;
                if (len > 0 && len <= 4096)
                    source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(textBegin, (int)len));
            }
            else
            {
                source = Marshal.PtrToStringUTF8((nint)textBegin);
            }

            if (!string.IsNullOrEmpty(source))
            {
                // 取得中もこの描画経路を対象側へ追加する。
                if (captureEnabled || baselineCaptureEnabled)
                    CapturePointer(textBegin, textEnd, "DrawListAddText");

                if (TryGetTranslationForPlugin(DalamudActProfile.PluginName, source, out var translated) &&
                    !string.IsNullOrWhiteSpace(translated) &&
                    !string.Equals(source, translated, StringComparison.Ordinal))
                {
                    var bytes = Encoding.UTF8.GetBytes(translated + "\0");
                    fixed (byte* p = bytes)
                    {
                        drawListAddTextVec2Hook!.Original(drawList, pos, col, p, null);
                        Interlocked.Increment(ref translatedCount);
                        return;
                    }
                }
            }
        }
        catch
        {
            // 失敗時は元の描画をそのまま通す。
        }

        drawListAddTextVec2Hook!.Original(drawList, pos, col, textBegin, textEnd);
    }

    private void RenderTextDetour(Vector2 pos, byte* text, byte* textEnd, byte hideTextAfterHash)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderText"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                renderTextHook!.Original(pos, p, p + bytes.Length, hideTextAfterHash);
                return;
            }
        }
        renderTextHook!.Original(pos, text, textEnd, hideTextAfterHash);
    }

    private void RenderTextWrappedDetour(Vector2 pos, byte* text, byte* textEnd, float wrapWidth)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderTextWrapped"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                renderTextWrappedHook!.Original(pos, p, p + bytes.Length, wrapWidth);
                return;
            }
        }
        renderTextWrappedHook!.Original(pos, text, textEnd, wrapWidth);
    }

    private void RenderTextClippedDetour(
        Vector2 posMin, Vector2 posMax, byte* text, byte* textEnd,
        Vector2* textSizeIfKnown, Vector2 align, NativeImRect* clipRect)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(text, textEnd, "RenderTextClipped"); } catch { }

        if (!drawingOwnUi && TryTranslateRenderPointerForCurrentWindow(text, textEnd, out var translated, out var pluginName))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated);
            fixed (byte* p = bytes)
            {
                // 翻訳後は元の英語文字列用サイズキャッシュを使わず再計算させる。
                renderTextClippedHook!.Original(posMin, posMax, p, p + bytes.Length, null, align, clipRect);
                return;
            }
        }

        renderTextClippedHook!.Original(posMin, posMax, text, textEnd, textSizeIfKnown, align, clipRect);
    }

    private bool IsCurrentWindowForAnyEnabledPlugin()
    {
        if (windowStack == null || windowStack.Count == 0) return false;
        foreach (var (pluginName, state) in config.Plugins)
        {
            if (!state.Enabled) continue;
            foreach (var windowName in windowStack)
                if (IsTargetWindow(pluginName, windowName)) return true;
        }
        return false;
    }

    private byte BeginTabItemDetour(byte* label, byte* pOpen, int flags)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "TabItem"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return beginTabItemHook!.Original(p, pOpen, flags);
        }
        return beginTabItemHook!.Original(label, pOpen, flags);
    }

    // v0.0.69: Allagan Tools の設定画面はウィンドウ名が汎用的な "Configuration" のため、
    // ウィンドウ名だけでは所有プラグインを安全に判定できない。
    // メニューバー固有の "Wizard" を同一フレームで確認した時だけ InventoryTools と確定する。
    // 判定条件そのものは InventoryToolsProfile が持つ。ここはスタックの差し替えだけを行う。
    private void DetectInventoryToolsConfigurationOwner(byte* label)
    {
        if (drawingOwnUi || label == null) return;
        if (!InventoryToolsProfile.IsConfigurationWindow(CurrentWindowName)) return;
        if (!config.Plugins.TryGetValue(InventoryToolsProfile.PluginName, out var state) || !state.Enabled) return;

        string? source;
        try { source = Marshal.PtrToStringUTF8((nint)label); }
        catch { return; }
        if (!InventoryToolsProfile.IsConfigurationOwnerMenu(source)) return;

        if (windowOwnerStack is not { Count: > 0 }) return;
        windowOwnerStack.Pop();
        windowOwnerStack.Push(InventoryToolsProfile.PluginName);
        lastExplicitWindowOwner = InventoryToolsProfile.PluginName;
        lastExplicitWindowOwnerTick = Environment.TickCount64;
    }

    private byte BeginMenuDetour(byte* label, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "BeginMenu"); } catch { }
        DetectInventoryToolsConfigurationOwner(label);

        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return beginMenuHook!.Original(p, enabled);
        }
        return beginMenuHook!.Original(label, enabled);
    }

    private byte MenuItemBoolDetour(byte* label, byte* shortcut, byte selected, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "MenuItem"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return menuItemBoolHook!.Original(p, shortcut, selected, enabled);
        }
        return menuItemBoolHook!.Original(label, shortcut, selected, enabled);
    }

    private byte MenuItemBoolPtrDetour(byte* label, byte* shortcut, byte* pSelected, byte enabled)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "MenuItemPtr"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, true, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var display = MakeDisplayOnlyInteractiveLabel(label, translated);
            var bytes = Encoding.UTF8.GetBytes(display + "\0");
            fixed (byte* p = bytes) return menuItemBoolPtrHook!.Original(p, shortcut, pSelected, enabled);
        }
        return menuItemBoolPtrHook!.Original(label, shortcut, pSelected, enabled);
    }

    private void SeparatorTextDetour(byte* label)
    {
        try { if (!drawingOwnUi && (captureEnabled || baselineCaptureEnabled)) CapturePointer(label, null, "SeparatorText"); } catch { }
        if (!drawingOwnUi && TryTranslatePointer(label, null, false, out var translated))
        {
            Interlocked.Increment(ref translatedCount);
            var bytes = Encoding.UTF8.GetBytes(translated + "\0");
            fixed (byte* ptr = bytes) separatorTextHook!.Original(ptr);
            return;
        }
        separatorTextHook!.Original(label);
    }

    // v0.0.18: ImGuiウィンドウ追跡。
    // igBegin/igEnd をフックして、現在どのウィンドウから描画された文字列かを判定する。
    private byte BeginDetour(byte* name, byte* pOpen, int flags)
    {
        var result = beginHook!.Original(name, pOpen, flags);

        string windowName = string.Empty;
        try
        {
            if (name != null)
                windowName = Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
        }
        catch { }

        windowStack ??= new Stack<string>();
        windowOwnerStack ??= new Stack<string>();

        // v0.0.66: Combo/Tooltip/Popup などの内部ウィンドウは元のプラグイン名を持たない。
        // 直前の通常ウィンドウの所有プラグインを継承し、追加プラグインでもドロップダウン候補や
        // ツールチップを背景側ではなく正しい対象へ取得できるようにする。
        var owner = ResolveWindowOwner(windowName);

        // v0.0.69: Allagan Tools などは、設定画面の内部を Child Window に分割して描画する。
        // 親ウィンドウで対象プラグインを確定できていても、子ウィンドウ名（例: Menu）自体には
        // プラグイン固有名が含まれないため、従来は RenderText 系の所有判定が外れていた。
        // ImGui の Begin が親ウィンドウの描画中にネストしている場合だけ、直近の親所有者を継承する。
        // 辞書検索や部分一致は追加せず、Stack.Peek() 1回だけの軽量な所属継承。
        if (string.IsNullOrWhiteSpace(owner) && windowOwnerStack.Count > 0)
            owner = windowOwnerStack.Peek();

        if (IsTransientImGuiWindow(windowName))
        {

            // v0.0.66:
            // Tooltip/Combo/Popup は親ウィンドウとは別の ImGui Window として描画されるため、
            // 直前の対象プラグイン所有者を短時間だけ継承する。
            // 無関係なウィンドウで lastExplicitWindowOwner を空にしないことが重要。
            if (string.IsNullOrWhiteSpace(owner) &&
                !string.IsNullOrWhiteSpace(lastExplicitWindowOwner) &&
                Environment.TickCount64 - lastExplicitWindowOwnerTick <= 150)
            {
                owner = lastExplicitWindowOwner;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(owner))
            {
                lastExplicitWindowOwner = owner;
                lastExplicitWindowOwnerTick = Environment.TickCount64;
            }
        }

        windowStack.Push(windowName);
        windowOwnerStack.Push(owner);

        // JP Helper自身の内部ウィンドウは検出一覧へ出さない。
        if (!drawingOwnUi && !string.IsNullOrWhiteSpace(windowName))
            seenWindows.AddOrUpdate(windowName, 1, (_, count) => count + 1);

        return result;
    }

    private void EndDetour()
    {
        try
        {
            endHook!.Original();
        }
        finally
        {
            if (windowStack is { Count: > 0 })
            {
                // RSRは1画面を複数の子ウィンドウで構成する。
                // 子ウィンドウのEndごとにメニュー状態を消すと所属情報が失われるため、
                // ここではウィンドウスタックだけ戻し、ナビゲーション状態は取得停止/クリア時まで保持する。
                windowStack.Pop();
                if (windowOwnerStack is { Count: > 0 }) windowOwnerStack.Pop();
            }
        }
    }

    private static string CurrentWindowName
        => windowStack is { Count: > 0 } ? windowStack.Peek() : string.Empty;

    private static string CurrentWindowOwner
        => windowOwnerStack is { Count: > 0 } ? windowOwnerStack.Peek() : string.Empty;

    private static bool IsTransientImGuiWindow(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;
        var w = windowName.Trim();
        return w.StartsWith("##Combo_", StringComparison.Ordinal)
            || w.StartsWith("##Tooltip_", StringComparison.Ordinal)
            || w.StartsWith("##Popup_", StringComparison.Ordinal)
            || w.StartsWith("##Menu_", StringComparison.Ordinal)
            || w.StartsWith("##ContextMenu_", StringComparison.Ordinal);
    }

    private string ResolveWindowOwner(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return string.Empty;
        foreach (var (pluginName, state) in config.Plugins)
        {
            if (!state.TranslationTarget && !state.Enabled) continue;
            if (IsTargetWindow(pluginName, windowName)) return pluginName;
        }
        return string.Empty;
    }

    private bool IsCurrentWindowOwnedBy(string pluginName)
        => string.Equals(CurrentWindowOwner, pluginName, StringComparison.Ordinal)
           || IsTargetWindow(pluginName, CurrentWindowName);

    private bool IsTargetWindow(string pluginName, string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return false;

        var customWindowKeyword = config.Plugins.TryGetValue(pluginName, out var state) ? state.WindowKeyword : null;
        return PluginProfileRegistry.MatchesWindow(pluginName, windowName.Trim(), customWindowKeyword);
    }

    private void UpdateRsrNavigationContext(byte* label, bool selected)
        => RsrNavigationTracker.Observe(label, selected, captureEnabled, capturePlugin, CurrentWindowName);

    private void UpdateRsrNavigationContextAfterClick(string raw)
        => RsrNavigationTracker.ObserveAfterClick(raw, captureEnabled, capturePlugin);

    private static string VisibleLabel(string source)
    {
        var marker = source.IndexOf("##", StringComparison.Ordinal);
        return marker > 0 ? source[..marker] : source;
    }

    private static string MakeDisplayOnlyInteractiveLabel(byte* originalLabel, string translatedVisible)
    {
        var original = originalLabel == null ? string.Empty : Marshal.PtrToStringUTF8((nint)originalLabel) ?? string.Empty;
        if (string.IsNullOrEmpty(original)) return translatedVisible;

        // ImGuiの ### は「表示文字」と「ID」を分離できる。
        // translated###original とすることで、見た目だけ翻訳しWidget IDは原文と同じに保つ。
        return translatedVisible + "###" + original;
    }

    private bool TryTranslatePointer(byte* begin, byte* end, bool preserveImGuiId, out string translated)
    {
        translated = string.Empty;
        if (begin == null) return false;
        string? source;
        if (end != null && end >= begin)
        {
            var len = end - begin; if (len <= 0 || len > 4096) return false;
            source = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len));
        }
        else source = Marshal.PtrToStringUTF8((nint)begin);
        if (string.IsNullOrEmpty(source)) return false;

        // まず従来どおり完全一致。既存挙動を変えない。
        if (TryTranslate(source, out translated)) return true;

        // v0.0.66:
        // TreeNode/Button/Checkbox/RadioButton 等の動的表示文字列にも部分翻訳を適用する。
        // 例: "01/21 白魔法师" -> "01/21 白魔道士"
        //
        // 重要:
        // - 現在のウィンドウに所属する有効プラグインの辞書だけを使う
        // - ## / ### 以降の ImGui ID は置換対象にしない
        // - 設定値、Enum、保存値、コマンドには触れず「表示ラベル」だけを差し替える
        var currentWindow = CurrentWindowName;
        if (string.IsNullOrWhiteSpace(currentWindow)) return false;

        string pluginName = string.Empty;
        foreach (var (name, state) in config.Plugins)
        {
            if (!state.Enabled) continue;
            if (string.Equals(CurrentWindowOwner, name, StringComparison.Ordinal) || IsTargetWindow(name, currentWindow))
            {
                pluginName = name;
                break;
            }
        }
        if (string.IsNullOrEmpty(pluginName)) return false;

        if (InventoryToolsProfile.TryTranslateDynamic(pluginName, source, out translated)) return true;
        if (ArtisanProfile.TryTranslateDynamic(pluginName, source, preserveImGuiId, out translated)) return true;

        // v0.0.66: Button/Checkbox/TreeNode/Selectable等も、部分一致はDalamudACTだけ。
        // 通常プラグインは上の完全一致辞書検索だけで終了する。
        if (!AllowsPartialTranslation(pluginName)) return false;

        if (!config.Plugins.TryGetValue(pluginName, out var pluginState)) return false;

        var idMarker = source.IndexOf("##", StringComparison.Ordinal);
        var visible = idMarker >= 0 ? source[..idMarker] : source;
        if (string.IsNullOrEmpty(visible)) return false;

        var candidates = GetDictionaryCatalog(pluginName)
            .Select(kv => (
                Source: kv.Key,
                Ja: pluginState.UserOverrides.TryGetValue(kv.Key, out var userJa) && !string.IsNullOrWhiteSpace(userJa)
                    ? userJa
                    : kv.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x.Source) && !string.IsNullOrWhiteSpace(x.Ja))
            .Where(x => !x.Source.Contains("##", StringComparison.Ordinal) && !x.Source.Contains("###", StringComparison.Ordinal))
            .Where(x => !x.Ja.Contains("##", StringComparison.Ordinal) && !x.Ja.Contains("###", StringComparison.Ordinal))
            .Where(x => visible.Contains(x.Source, StringComparison.Ordinal))
            .OrderByDescending(x => x.Source.Length)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0) return false;

        var replaced = visible;
        foreach (var x in candidates)
            replaced = replaced.Replace(x.Source, x.Ja, StringComparison.Ordinal);

        if (string.Equals(replaced, visible, StringComparison.Ordinal)) return false;

        // interactive系は呼び出し側 MakeDisplayOnlyInteractiveLabel() が
        // 元ラベル全体を ###original として保持するため、ここでは表示部分だけ返す。
        translated = replaced;
        return true;
    }

    private bool TryTranslate(string source, out string translated)
    {
        const string counterPrefix = "RSR has helped you by clicking actions ";
        const string counterSuffix = " times.";
        if (source.StartsWith(counterPrefix, StringComparison.Ordinal) && source.EndsWith(counterSuffix, StringComparison.Ordinal))
        {
            var count = source[counterPrefix.Length..^counterSuffix.Length];
            translated = $"RSRがアクションを実行した回数: {count}回";
            return true;
        }

        foreach (var (pluginName, state) in config.Plugins)
        {
            if (!state.Enabled) continue;
            if (TryGetTranslationForPlugin(pluginName, source, out translated)) return true;
        }
        translated = string.Empty; return false;
    }

    private void CallTextOriginal(string translated) { var bytes = Encoding.UTF8.GetBytes(translated); fixed (byte* ptr = bytes) textHook!.Original(ptr, ptr + bytes.Length); }
    private byte CallCheckboxOriginal(string translated, byte* value) { var bytes = Encoding.UTF8.GetBytes(translated + "\0"); fixed (byte* ptr = bytes) return checkboxHook!.Original(ptr, value); }
    private byte CallButtonOriginal(string translated, Vector2 size) { var bytes = Encoding.UTF8.GetBytes(translated + "\0"); fixed (byte* ptr = bytes) return buttonHook!.Original(ptr, size); }

    private void CapturePointer(byte* begin, byte* end, string kind)
    {
        if (begin == null) return;
        string? text;
        if (end != null && end >= begin) { var len = end - begin; if (len <= 0 || len > 4096) return; text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(begin, (int)len)); }
        else text = Marshal.PtrToStringUTF8((nint)begin);
        if (!LooksUseful(text)) return;

        // v0.0.14: 背景と対象プラグインを、文字列差分ではなく ImGui ウィンドウ単位で同時分離する。
        var currentWindow = CurrentWindowName;
        var isTargetWindow = captureEnabled && IsCurrentWindowOwnedBy(capturePlugin);

        // v0.0.25: RSRサイドバーは「現在メニュー判定専用」。本文辞書へは登録しない。
        // これで Actions / Auto / Basic ... が各メニュー配下に重複する現象を除外する。
        if (isTargetWindow && RsrProfile.IsSideBarWindow(capturePlugin, currentWindow))
            return;

        if (captureEnabled && isTargetWindow)
        {
            // RSR左メニューは直前に描画されるため、最後に見つけたselectedな
            // 確認済みメニュー候補を、RSR本体の文字列を取得する瞬間に確定する。
            RsrNavigationTracker.CommitPendingMenu(capturePlugin);

            var menu = RsrProfile.MatchesPluginName(capturePlugin) ? RsrNavigationTracker.CurrentMenu : string.Empty;
            var section = RsrProfile.MatchesPluginName(capturePlugin) ? RsrNavigationTracker.CurrentSection : string.Empty;
            var target = pluginCaptured[capturePlugin];
            var contextKey = string.Concat(menu, "\u001f", section, "\u001f", text);
            target.AddOrUpdate(contextKey,
                _ => new CapturedItem(text!, kind, 1, menu, section, currentWindow),
                (_, old) => old with { Count = old.Count + 1, Kind = MergeKind(old.Kind, kind) });
            return;
        }

        if (baselineCaptureEnabled)
        {
            var contextKey = string.Concat(currentWindow, "\u001f", text);
            backgroundCaptured.AddOrUpdate(contextKey,
                _ => new CapturedItem(text!, kind, 1, string.Empty, string.Empty, currentWindow),
                (_, old) => old with { Count = old.Count + 1, Kind = MergeKind(old.Kind, kind) });
        }
    }

    private static string MergeKind(string oldKind, string newKind) => oldKind.Contains(newKind, StringComparison.Ordinal) ? oldKind : $"{oldKind}/{newKind}";
    private static bool LooksUseful(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim(); if (s.Length < 2 || s.Length > 500 || s.StartsWith("##", StringComparison.Ordinal)) return false;
        // v0.0.28: 英語だけでなく、中国語などのCJK文字も原文候補として取得する。
        return s.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' || (c >= '\u3400' && c <= '\u9fff'));
    }

    private void OnCommand(string command, string args) => windowOpen = true;
    private void OpenUi() => windowOpen = true;

    private enum ButtonRole { Primary, Success, Warning, Danger, Neutral }

    private static bool ActionButton(string label, ButtonRole role)
    {
        var color = role switch
        {
            ButtonRole.Primary => new Vector4(0.18f, 0.42f, 0.68f, 1.00f),
            ButtonRole.Success => new Vector4(0.18f, 0.52f, 0.34f, 1.00f),
            ButtonRole.Warning => new Vector4(0.72f, 0.43f, 0.12f, 1.00f),
            ButtonRole.Danger => new Vector4(0.66f, 0.22f, 0.22f, 1.00f),
            _ => new Vector4(0.30f, 0.33f, 0.38f, 1.00f),
        };
        var hovered = new Vector4(MathF.Min(color.X + 0.10f, 1f), MathF.Min(color.Y + 0.10f, 1f), MathF.Min(color.Z + 0.10f, 1f), color.W);
        var active = new Vector4(MathF.Max(color.X - 0.06f, 0f), MathF.Max(color.Y - 0.06f, 0f), MathF.Max(color.Z - 0.06f, 0f), color.W);
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var pressed = ImGui.Button(label);
        ImGui.PopStyleColor(3);
        return pressed;
    }

    private void Draw()
    {
        if (!windowOpen) return;
        // Plugin JP Helper 自身のUIは翻訳・取得対象にしない。
        // これにより辞書左列の「英語原文」は必ず原文のまま表示される。
        drawingOwnUi = true;
        try
        {
            ImGui.SetNextWindowSize(new Vector2(920, 650), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Plugin JP Helper v0.1.0", ref windowOpen)) { ImGui.End(); return; }

            ImGui.TextWrapped("プラグインの表示を日本語化する翻訳辞書を管理します。初めて使う場合は「未翻訳・取得」から対象プラグインを追加してください。");
            ImGui.Separator();

            ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
            if (ImGui.BeginTabBar("mainTabs"))
            {
                if (ImGui.BeginTabItem("翻訳辞書")) { DrawDictionaryTab(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("未翻訳・取得")) { DrawCaptureTab(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            ImGui.PopStyleColor(3);
            ImGui.End();
        }
        finally
        {
            drawingOwnUi = false;
        }
    }

    private IEnumerable<(string Key, string Label)> GetRsrDictionaryTabs()
    {
        yield return ("Main", "メイン");
        var known = new HashSet<string>(RsrNavigationVocabulary.BaseDictionaryTabs.Select(x => x.Key), StringComparer.Ordinal) { "Uncategorized" };
        var dynamicMenus = config.Plugins["RSR"].Locations.Values
            .Select(x => x.Menu)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !known.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var menu in dynamicMenus) yield return (menu, menu);
        foreach (var tab in RsrNavigationVocabulary.BaseDictionaryTabs.Skip(1)) yield return tab;
        yield return ("Uncategorized", "未分類");
    }

    private void RemoveTranslationTarget(string pluginName)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return;
        state.TranslationTarget = false;
        state.Enabled = false;
        SaveConfig();

        var next = config.Plugins.Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginProfileRegistry.SortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(next)) selectedPlugin = capturePlugin = next;
        csvStatus = $"{pluginName} を翻訳対象から外しました。辞書データ自体は削除していません。";
    }

    private void DrawDictionaryTab()
    {
        var pluginNames = config.Plugins
            .Where(x => x.Value.TranslationTarget)
            .Select(x => x.Key)
            .OrderBy(x => PluginProfileRegistry.SortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (pluginNames.Length == 0)
        {
            ImGui.TextDisabled("翻訳対象がありません。「未翻訳・取得」タブからインストール済みプラグインを追加してください。");
            return;
        }
        if (!pluginNames.Contains(selectedPlugin, StringComparer.Ordinal)) selectedPlugin = pluginNames[0];
        foreach (var name in pluginNames)
        {
            if (ImGui.RadioButton($"{name}##dictPlugin", selectedPlugin == name)) selectedPlugin = name;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        var state = config.Plugins[selectedPlugin];
        var enabled = state.Enabled;
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.28f, 0.78f, 0.48f, 1.00f));
        if (ImGui.Checkbox($"{selectedPlugin} の日本語化を有効##enabled", ref enabled)) { state.Enabled = enabled; SaveConfig(); }
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var catalogCount = GetDictionaryCatalog(selectedPlugin).Count();
        ImGui.TextUnformatted($"原文 {catalogCount} 件 / ユーザー訳 {state.UserOverrides.Count} 件");
        ImGui.SameLine();
        if (ActionButton("翻訳対象から外す", ButtonRole.Danger))
        {
            RemoveTranslationTarget(selectedPlugin);
            return;
        }
        if (config.CleanSlateMode) ImGui.TextWrapped("完全リセット後の取得済み原文を表示しています。旧標準訳は使いません。");

        ImGui.TextDisabled("配布されたCSVを使う場合は「CSVを選択して読み込む」だけで利用できます。");
        if (ActionButton("CSVを選択して読み込む", ButtonRole.Primary))
        {
            var chosen = ShowCsvOpenDialog();
            if (!string.IsNullOrWhiteSpace(chosen)) csvStatus = ImportDictionaryCsv(selectedPlugin, chosen);
        }
        ImGui.SameLine();
        if (ActionButton("再読み込み", ButtonRole.Primary))
        {
            csvStatus = string.IsNullOrWhiteSpace(state.LastCsvPath)
                ? "先にCSVを選択してください。"
                : ImportDictionaryCsv(selectedPlugin, state.LastCsvPath);
        }
        ImGui.SameLine();
        if (ActionButton("名前を付けて書き出し", ButtonRole.Success))
        {
            var savePath = ShowCsvSaveDialog(selectedPlugin, state.LastCsvPath);
            if (!string.IsNullOrWhiteSpace(savePath)) csvStatus = ExportDictionaryCsv(selectedPlugin, savePath);
        }
        ImGui.SameLine();
        if (ActionButton("保存フォルダーを開く", ButtonRole.Neutral)) csvStatus = OpenDictionaryFolder();

        if (!string.IsNullOrWhiteSpace(state.LastCsvPath))
            ImGui.TextDisabled($"指定中CSV: {state.LastCsvPath}");
        else
            ImGui.TextDisabled("指定中CSV: なし");

        if (ImGui.Checkbox("未訳だけ表示", ref showUntranslatedOnly)) { }
        if (!string.IsNullOrWhiteSpace(csvStatus)) ImGui.TextWrapped(csvStatus);

        ImGui.SetNextItemWidth(320); ImGui.InputTextWithHint("##dictfilter", "原文・訳を絞り込み", ref filter, 256);
        ImGui.SameLine();
        var sortLabels = new[] { "原文", "画面内項目", "日本語訳", "未訳優先" };
        ImGui.SetNextItemWidth(140);
        if (ImGui.BeginCombo("並び順##dictSort", sortLabels[Math.Clamp(dictionarySortMode, 0, sortLabels.Length - 1)]))
        {
            for (var i = 0; i < sortLabels.Length; i++)
            {
                var selected = dictionarySortMode == i;
                if (ImGui.Selectable($"{sortLabels[i]}##dictSort{i}", selected)) dictionarySortMode = i;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button(dictionarySortAscending ? "昇順" : "降順")) dictionarySortAscending = !dictionarySortAscending;

        if (selectedPlugin == "RSR")
        {
            ImGui.TextDisabled("RSR本体の左メニューと同じ順番で表示しています。");
            if (ImGui.BeginTabBar("rsrDictionaryTabs", ImGuiTabBarFlags.FittingPolicyScroll))
            {
                var menuCounts = GetDictionaryMenuCounts("RSR");
                foreach (var (key, label) in GetRsrDictionaryTabs())
                {
                    var count = menuCounts.TryGetValue(key, out var tabCount) ? tabCount : 0;
                    if (ImGui.BeginTabItem($"{label} ({count})"))
                    {
                        DrawDictionaryTable(state, key);
                        ImGui.EndTabItem();
                    }
                }
                ImGui.EndTabBar();
            }
        }
        else
        {
            DrawDictionaryTable(state, null);
        }
    }

    private string GetSortableHeaderLabel(string label, int sortMode)
    {
        if (dictionarySortMode != sortMode) return label;
        return $"{label} {(dictionarySortAscending ? "▲" : "▼")}";
    }

    private void SetDictionarySortFromHeader(int sortMode)
    {
        if (dictionarySortMode == sortMode)
            dictionarySortAscending = !dictionarySortAscending;
        else
        {
            dictionarySortMode = sortMode;
            dictionarySortAscending = true;
        }
    }

    private void DrawDictionaryTable(PluginDictionaryState state, string? category)
    {
        if (ImGui.BeginTable($"dict_{selectedPlugin}_{category ?? "all"}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -35)))
        {
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("原文（英語/中国語）", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableSetupColumn("日本語訳（編集可）", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 90);

            // ヘッダー行を縦スクロール時も固定。
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableSetColumnIndex(0);
            ImGui.TableHeader(GetSortableHeaderLabel("画面内項目", 1));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(1);

            ImGui.TableSetColumnIndex(1);
            ImGui.TableHeader(GetSortableHeaderLabel("原文（英語/中国語）", 0));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(0);

            ImGui.TableSetColumnIndex(2);
            ImGui.TableHeader(GetSortableHeaderLabel("日本語訳（編集可）", 2));
            if (ImGui.IsItemClicked()) SetDictionarySortFromHeader(2);

            ImGui.TableSetColumnIndex(3);
            ImGui.TableHeader("操作");

            var rows = GetDictionaryCatalog(selectedPlugin)
                .Select(kv => new
                {
                    Key = kv.Key,
                    DefaultJa = kv.Value,
                    CurrentJa = state.UserOverrides.TryGetValue(kv.Key, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov : kv.Value,
                    Section = selectedPlugin == "RSR" ? GetRsrSection(kv.Key) : string.Empty,
                    Menu = selectedPlugin == "RSR" ? GetRsrMenuCategory(kv.Key) : string.Empty,
                })
                .Where(row => category == null || row.Menu == category)
                .Where(row => !showUntranslatedOnly || string.IsNullOrWhiteSpace(row.CurrentJa))
                .Where(row => string.IsNullOrWhiteSpace(filter)
                    || row.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.CurrentJa.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || row.Section.Contains(filter, StringComparison.OrdinalIgnoreCase));

            rows = dictionarySortMode switch
            {
                1 => dictionarySortAscending
                    ? rows.OrderBy(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                2 => dictionarySortAscending
                    ? rows.OrderBy(x => x.CurrentJa, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.CurrentJa, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                3 => dictionarySortAscending
                    ? rows.OrderBy(x => string.IsNullOrWhiteSpace(x.CurrentJa) ? 0 : 1).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => string.IsNullOrWhiteSpace(x.CurrentJa) ? 0 : 1).ThenByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
                _ => dictionarySortAscending
                    ? rows.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(x => x.Key, StringComparer.OrdinalIgnoreCase),
            };

            // v0.0.66: 大きい辞書（特にRSR）で全行のInputText/Buttonを毎フレーム生成すると重くなる。
            // 並び替え後の行を配列化し、ImGuiListClipperで画面に見えている行だけ描画する。
            var rowList = rows.ToArray();
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rowList.Length);
            while (clipper.Step())
            {
                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var row = rowList[rowIndex];
                    var key = $"{selectedPlugin}\u001f{row.Key}";
                    if (!editBuffers.TryGetValue(key, out var edit)) edit = row.CurrentJa;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(row.Section);
                    ImGui.TableSetColumnIndex(1);

                    var sourceText = row.Key;
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputText($"##source{key.GetHashCode()}", ref sourceText, 4096, ImGuiInputTextFlags.ReadOnly);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputText($"##edit{key.GetHashCode()}", ref edit, 1024))
                        editBuffers[key] = edit;

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"保存##{key.GetHashCode()}"))
                    {
                        if (string.IsNullOrWhiteSpace(edit))
                        {
                            state.UserOverrides.Remove(row.Key);
                            editBuffers[key] = row.DefaultJa;
                        }
                        else
                        {
                            state.UserOverrides[row.Key] = edit;
                            editBuffers[key] = edit;
                        }
                        SaveConfig();
                    }

                    if (state.UserOverrides.ContainsKey(row.Key))
                    {
                        if (ImGui.SmallButton($"訳を消す##r{key.GetHashCode()}"))
                        {
                            state.UserOverrides.Remove(row.Key);
                            editBuffers[key] = row.DefaultJa;
                            SaveConfig();
                        }
                    }
                }
            }
            clipper.End();

            ImGui.EndTable();
        }
    }

    private string GetRsrMenuCategory(string key)
    {
        if (config.Plugins.TryGetValue("RSR", out var state) && state.Locations.TryGetValue(key, out var location) && !string.IsNullOrWhiteSpace(location.Menu))
            return location.Menu;

        // v0.0.22: 意味からの推測分類は廃止。再取得できた所属だけを正とする。
        return "Uncategorized";
    }

    private string GetRsrSection(string key)
    {
        if (config.Plugins.TryGetValue("RSR", out var state) && state.Locations.TryGetValue(key, out var location))
            return location.Section ?? string.Empty;
        return string.Empty;
    }

    private void ApplyCapturedLocations(string pluginName)
    {
        if (!config.Plugins.TryGetValue(pluginName, out var state)) return;
        var items = pluginCaptured[pluginName].Values.ToArray();
        foreach (var group in items.GroupBy(x => x.Text, StringComparer.Ordinal))
        {
            var contexts = group
                .Where(x => !string.IsNullOrWhiteSpace(x.Menu))
                .Select(x => new DictionaryLocation { Menu = x.Menu, Section = x.Section ?? string.Empty })
                .Distinct()
                .ToArray();

            // 取得した原文は、所属が取れない場合も辞書カタログへ必ず登録する。
            // 1箇所だけ特定できた場合のみ所属を付け、0件/複数箇所は未分類として保持する。
            // これによりBMR/BMなど、メニュー判定を持たない対象でも取得停止後すぐ翻訳辞書で編集できる。
            if (contexts.Length == 1) state.Locations[group.Key] = contexts[0];
            else state.Locations[group.Key] = new() { Menu = string.Empty, Section = string.Empty };
        }
        SaveConfig();
    }

    private void DrawCaptureTab()
    {

        ImGui.TextWrapped("新しい翻訳辞書を作るときに使います。まず「インストール済みプラグインから追加」で日本語化したいプラグインを追加してください。");
        ImGui.TextDisabled("追加後は「取得開始」→対象プラグインの画面やツールチップを一通り表示→「取得停止」の順で操作します。ツールチップ等も可能な限り対象プラグインへ自動分類します。");

        ImGui.Separator();
        ImGui.TextUnformatted("インストール済みプラグインから追加");
        ImGui.TextDisabled("日本語化したいプラグインを選択して追加します。");
        var installed = pluginInterface.InstalledPlugins
            .Where(p => !string.Equals(p.InternalName, pluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedInstalled = installed.FirstOrDefault(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal));
        var installedPreview = selectedInstalled == null ? "選択してください" : $"{selectedInstalled.Name} ({selectedInstalled.InternalName})";
        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo("##installedPluginSelect", installedPreview))
        {
            foreach (var p in installed)
            {
                var already = config.Plugins.TryGetValue(p.InternalName, out var existing) && existing.TranslationTarget;
                var label = $"{p.Name} ({p.InternalName}){(already ? "  [追加済み]" : string.Empty)}{(p.IsLoaded ? string.Empty : "  [停止中]")}";
                var selected = string.Equals(installedPluginSelection, p.InternalName, StringComparison.Ordinal);
                if (ImGui.Selectable($"{label}##installed_{p.InternalName}", selected)) installedPluginSelection = p.InternalName;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ActionButton("翻訳対象に追加", ButtonRole.Primary) && !captureEnabled) AddInstalledPluginTarget();

        if (ImGui.TreeNode("手入力で追加（自動判定できない場合のみ）"))
        {
            ImGui.TextDisabled("通常は使いません。自動判定できないプラグインだけ手入力します。");
            ImGui.SetNextItemWidth(180); ImGui.InputTextWithHint("##customPlugin", "プラグイン名", ref customPluginName, 64);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(260); ImGui.InputTextWithHint("##customWindow", "ウィンドウ名キーワード", ref customWindowKeyword, 128);
            ImGui.SameLine();
            if (ActionButton("手入力で追加", ButtonRole.Primary) && !captureEnabled) AddCustomPlugin();
            ImGui.TreePop();
        }


        ImGui.Spacing();
        ImGui.TextUnformatted("翻訳対象プラグイン");
        var captureNames = config.Plugins.Where(x => x.Value.TranslationTarget).Select(x => x.Key)
            .OrderBy(x => PluginProfileRegistry.SortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (captureNames.Length > 0 && !captureNames.Contains(capturePlugin, StringComparer.Ordinal)) capturePlugin = captureNames[0];
        foreach (var name in captureNames)
        {
            if (ImGui.RadioButton($"{name}##capturePlugin", capturePlugin == name) && !captureEnabled) capturePlugin = name;
            ImGui.SameLine();
        }
        ImGui.NewLine();

        if (!string.IsNullOrWhiteSpace(capturePlugin) && !captureEnabled)
        {
            if (ActionButton("選択中を翻訳対象から外す", ButtonRole.Danger))
                RemoveTranslationTarget(capturePlugin);
            ImGui.SameLine();
            ImGui.TextDisabled("辞書データは削除せず、翻訳対象からだけ外します。");
        }

        if (string.IsNullOrWhiteSpace(capturePlugin))
        {
            ImGui.TextDisabled("先に翻訳対象プラグインを追加してください。");
            return;
        }

        EnsureCaptureDictionary(capturePlugin);
        var current = pluginCaptured[capturePlugin];

        ImGui.Separator();
        ImGui.TextUnformatted("未翻訳の取得");
        if (captureEnabled || baselineCaptureEnabled)
        {
            if (ActionButton("取得停止", ButtonRole.Warning))
            {
                captureEnabled = false;
                baselineCaptureEnabled = false;
                ApplyCapturedLocations(capturePlugin);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{capturePlugin} を取得中");
        }
        else
        {
            if (ActionButton("取得開始", ButtonRole.Success))
            {
                current.Clear();
                backgroundCaptured.Clear();
                RsrNavigationTracker.Reset();
                baselineCaptureEnabled = true;
                captureEnabled = true;
                SaveConfig();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("対象プラグインの画面を一通り開いて操作してください。");
        }

        if (ActionButton("一覧クリア", ButtonRole.Danger))
        {
            current.Clear();
            backgroundCaptured.Clear();
        }
        ImGui.SameLine();
        if (ActionButton("対象分をコピー", ButtonRole.Primary))
            ImGui.SetClipboardText(BuildUntranslatedExport(capturePlugin));
        ImGui.SameLine();
        if (ActionButton("その他取得分をコピー", ButtonRole.Neutral))
            ImGui.SetClipboardText(BuildBackgroundExport());

        var untranslated = current.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        var bgUntranslated = backgroundCaptured.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase).ToArray();
        ImGui.TextUnformatted($"対象: 取得 {current.Count}件 / 未翻訳 {untranslated.Length}件    その他: 取得 {backgroundCaptured.Count}件 / 未翻訳 {bgUntranslated.Length}件");

        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.16f, 0.20f, 0.26f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.24f, 0.46f, 0.70f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.38f, 0.62f, 1.00f));
        if (ImGui.BeginTabBar("captureResultTabs"))
        {
            if (ImGui.BeginTabItem($"対象プラグイン ({untranslated.Length})"))
            {
                DrawCapturedTable($"plugin_{capturePlugin}", untranslated);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"その他取得分 ({bgUntranslated.Length})"))
            {
                DrawCapturedTable("other", bgUntranslated);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.PopStyleColor(3);
    }

    private static void DrawCapturedTable(string id, CapturedItem[] items)
    {
        if (ImGui.BeginTable($"untranslated_{id}", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, new Vector2(0, -25)))
        {
            ImGui.TableSetupColumn("メニュー", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("画面内項目", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("種類", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("未翻訳の英語", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var item in items)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.TextUnformatted(item.Menu);
                ImGui.TableSetColumnIndex(1); ImGui.TextUnformatted(item.Section);
                ImGui.TableSetColumnIndex(2); ImGui.TextUnformatted(item.Kind);
                ImGui.TableSetColumnIndex(3); ImGui.TextUnformatted(item.Count.ToString());
                ImGui.TableSetColumnIndex(4); ImGui.TextWrapped(item.Text);
            }
            ImGui.EndTable();
        }
    }

    private static bool ContainsJapanese(string text)
    {
        // v0.0.28: 漢字だけでは中国語と区別できないため、ひらがな/カタカナを含む場合だけ日本語扱いする。
        foreach (var c in text)
        {
            if (c >= '\u3040' && c <= '\u30ff') return true;
        }
        return false;
    }

    private bool IsKnown(string source)
    {
        var visible = source; var marker = source.IndexOf("##", StringComparison.Ordinal); if (marker > 0) visible = source[..marker];
        if (KeepAsIs.Labels.Contains(visible)) return true;
        if (ContainsJapanese(visible)) return true;
        if (RsrProfile.IsUntranslatableLiteral(source) || RsrProfile.IsProgressClickCount(visible)) return true;
        foreach (var (name, state) in config.Plugins) if (GetActiveStandardDictionary(name).ContainsKey(visible) || state.UserOverrides.ContainsKey(visible)) return true;
        return false;
    }

    private string BuildUntranslatedExport(string pluginName)
    {
        var sb = new StringBuilder(); sb.AppendLine($"# Plugin JP Helper {pluginName} untranslated v0.1.0");
        foreach (var item in pluginCaptured[pluginName].Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            sb.Append(item.Menu).Append('\t').Append(item.Section).Append('\t').Append(item.Kind).Append('\t').Append(item.Count).Append('\t').AppendLine(item.Text);
        return sb.ToString();
    }

    private string BuildBackgroundExport()
    {
        var sb = new StringBuilder(); sb.AppendLine("# Plugin JP Helper background v0.1.0");
        foreach (var item in backgroundCaptured.Values.Where(x => !IsKnown(x.Text)).OrderBy(x => x.Text, StringComparer.OrdinalIgnoreCase))
            sb.Append(item.Window).Append('\t').Append(item.Kind).Append('\t').Append(item.Count).Append('\t').AppendLine(item.Text);
        return sb.ToString();
    }

    private IReadOnlyDictionary<string, string> GetActiveStandardDictionary(string pluginName)
        => config.CleanSlateMode || !StandardDictionaries.ByPlugin.TryGetValue(pluginName, out var dict)
            ? EmptyDictionary
            : dict;

    private static readonly IReadOnlyDictionary<string, string> EmptyDictionary
        = new Dictionary<string, string>(StringComparer.Ordinal);

    private void FullResetAllData()
    {
        captureEnabled = false;
        baselineCaptureEnabled = false;
        backgroundCaptured.Clear();
        foreach (var dict in pluginCaptured.Values) dict.Clear();
        editBuffers.Clear();
        seenWindows.Clear();
        foreach (var state in config.Plugins.Values)
        {
            state.UserOverrides.Clear();
            state.Locations.Clear();
            state.Enabled = false;
        }
        config.Plugins["RSR"].Enabled = true;
        config.CleanSlateMode = true;
        config.CaptureSchemaVersion = 3;
        config.DataResetVersion = 24;
        RsrNavigationTracker.Reset();
        comboOpenDepth = 0;
        SaveConfig();
    }


    private void EnsureCaptureDictionaries()
    {
        foreach (var name in config.Plugins.Keys) EnsureCaptureDictionary(name);
    }

    private void EnsureCaptureDictionary(string name)
    {
        if (!pluginCaptured.ContainsKey(name)) pluginCaptured[name] = new ConcurrentDictionary<string, CapturedItem>(StringComparer.Ordinal);
    }



    private bool IsTranslatedShadowKey(string pluginName, string key)
    {
        // RenderText段階の翻訳後文字列が取得ログへ回り込み、
        // 「原文」候補としてLocationsへ残ることがある。RSRでは標準辞書の
        // 日本語訳と完全一致するだけのキーは原文ではないので一覧から除外する。
        // 標準辞書の正式なキーそのものは必ず残す。
        if (!RsrProfile.MatchesPluginName(pluginName)) return false;
        return RsrProfile.IsShadowKey(key, GetActiveStandardDictionary(pluginName));
    }

    private IEnumerable<KeyValuePair<string, string>> GetDictionaryCatalog(string pluginName)
        => GetDictionaryCatalogSnapshot(pluginName);

    private KeyValuePair<string, string>[] GetDictionaryCatalogSnapshot(string pluginName)
    {
        if (dictionaryCatalogCache.TryGetValue(pluginName, out var cached))
            return cached;

        if (!config.Plugins.TryGetValue(pluginName, out var state))
            return Array.Empty<KeyValuePair<string, string>>();

        var standard = GetActiveStandardDictionary(pluginName);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in state.Locations.Keys) keys.Add(key);
        foreach (var key in state.UserOverrides.Keys) keys.Add(key);
        foreach (var key in standard.Keys) keys.Add(key);

        var rows = new List<KeyValuePair<string, string>>(keys.Count);
        foreach (var key in keys)
        {
            if (IsTranslatedShadowKey(pluginName, key)) continue;
            var fallback = standard.TryGetValue(key, out var ja) ? ja : string.Empty;
            rows.Add(new KeyValuePair<string, string>(key, fallback));
        }

        var snapshot = rows.ToArray();
        dictionaryCatalogCache[pluginName] = snapshot;
        return snapshot;
    }

    private Dictionary<string, int> GetDictionaryMenuCounts(string pluginName)
    {
        if (dictionaryMenuCountCache.TryGetValue(pluginName, out var cached))
            return cached;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in GetDictionaryCatalogSnapshot(pluginName))
        {
            var menu = pluginName == "RSR" ? GetRsrMenuCategory(kv.Key) : string.Empty;
            counts[menu] = counts.TryGetValue(menu, out var n) ? n + 1 : 1;
        }

        dictionaryMenuCountCache[pluginName] = counts;
        return counts;
    }

    private void InvalidateDictionaryUiCache()
    {
        dictionaryCatalogCache.Clear();
        dictionaryMenuCountCache.Clear();
    }

    private void AddInstalledPluginTarget()
    {
        if (string.IsNullOrWhiteSpace(installedPluginSelection))
        {
            csvStatus = "追加するインストール済みプラグインを選択してください。";
            return;
        }

        var plugin = pluginInterface.InstalledPlugins.FirstOrDefault(p => string.Equals(p.InternalName, installedPluginSelection, StringComparison.Ordinal));
        if (plugin == null)
        {
            csvStatus = "選択したプラグインをインストール済み一覧から取得できませんでした。";
            return;
        }

        var key = plugin.InternalName;
        if (!config.Plugins.TryGetValue(key, out var state))
        {
            state = new PluginDictionaryState();
            config.Plugins[key] = state;
        }

        state.TranslationTarget = true;
        state.Enabled = true;
        var keywords = new[] { plugin.Name?.Trim(), plugin.InternalName?.Trim() }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ArtisanProfile.MatchesPluginName(plugin.InternalName) || ArtisanProfile.MatchesPluginName(plugin.Name))
            ArtisanProfile.EnsureWindowKeywords(keywords);
        state.WindowKeyword = string.Join("|", keywords);
        EnsureCaptureDictionary(key);
        selectedPlugin = capturePlugin = key;
        SaveConfig();
        csvStatus = $"{plugin.Name} ({plugin.InternalName}) を翻訳対象に追加しました。";
    }

    private void AddCustomPlugin()
    {
        var name = customPluginName.Trim();
        var keyword = customWindowKeyword.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(keyword))
        {
            csvStatus = "追加にはプラグイン名とウィンドウ名キーワードの両方が必要です。";
            return;
        }
        if (!config.Plugins.TryGetValue(name, out var state))
        {
            state = new PluginDictionaryState();
            config.Plugins[name] = state;
        }
        state.WindowKeyword = keyword;
        state.Enabled = true;
        state.TranslationTarget = true;
        EnsureCaptureDictionary(name);
        selectedPlugin = capturePlugin = name;
        customPluginName = customWindowKeyword = string.Empty;
        SaveConfig();
        csvStatus = $"対象 {name} を追加しました。";
    }

    private string CsvPath(string pluginName)
    {
        var safe = string.Concat(pluginName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Dictionaries");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, safe + ".csv");
    }

    private string DictionaryDirectory()
    {
        var dir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "Dictionaries");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string OpenDictionaryFolder()
    {
        try
        {
            var dir = DictionaryDirectory();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            return $"保存フォルダーを開きました: {dir}";
        }
        catch (Exception ex) { return $"保存フォルダーを開けませんでした: {ex.Message}"; }
    }

    private bool EnsureLatestBundledInventoryToolsCsv()
    {
        try
        {
            const string pluginName = InventoryToolsProfile.PluginName;

            if (!config.Plugins.TryGetValue(pluginName, out var state) || state == null)
            {
                state = new PluginDictionaryState
                {
                    Enabled = true,
                    TranslationTarget = true,
                    WindowKeyword = InventoryToolsProfile.DefaultWindowKeyword,
                };
                config.Plugins[pluginName] = state;
                EnsureCaptureDictionary(pluginName);
            }
            else
            {
                state.Enabled = true;
                state.TranslationTarget = true;
            }

            var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
            var latest = InventoryToolsProfile.FindLatestBundledPatch(assemblyDir);

            if (latest is not { } patch)
            {
                log.Warning($"[PluginJPHelper] InventoryTools bundled patch CSV not found in: {assemblyDir}");
                return false;
            }

            var fileName = Path.GetFileName(patch.Path);
            var configCsvPath = Path.Combine(DictionaryDirectory(), fileName);
            var alreadyImported = File.Exists(configCsvPath)
                && string.Equals(
                    Path.GetFileName(state.LastCsvPath ?? string.Empty),
                    fileName,
                    StringComparison.OrdinalIgnoreCase);

            if (alreadyImported)
            {
                log.Information($"[PluginJPHelper] InventoryTools bundled CSV already imported: {fileName}");
                return true;
            }

            File.Copy(patch.Path, configCsvPath, true);
            var result = ImportDictionaryCsv(pluginName, configCsvPath);
            if (result.StartsWith("CSV読込失敗", StringComparison.Ordinal)
                || result.StartsWith("CSVがありません", StringComparison.Ordinal))
            {
                log.Warning($"[PluginJPHelper] InventoryTools bundled CSV import failed: {result}");
                return false;
            }

            log.Information($"[PluginJPHelper] InventoryTools latest bundled CSV imported: v{patch.Version} / {result}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PluginJPHelper] InventoryTools bundled CSV import failed");
            return false;
        }
    }

    private void EnsureInitialRsrCsv()
    {
        try
        {
            var path = CsvPath("RSR");
            if (!File.Exists(path)) _ = ExportDictionaryCsv("RSR");
        }
        catch (Exception ex) { log.Warning(ex, "RSR初期CSVの作成に失敗しました"); }
    }

    private string ExportDictionaryCsv(string pluginName) => ExportDictionaryCsv(pluginName, CsvPath(pluginName));

    private string ExportDictionaryCsv(string pluginName, string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            var state = config.Plugins[pluginName];
            using var sw = new StreamWriter(fullPath, false, new UTF8Encoding(true));
            sw.WriteLine("Plugin,Menu,Section,Type,English,Japanese");
            foreach (var kv in GetDictionaryCatalog(pluginName).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                state.Locations.TryGetValue(kv.Key, out var loc);
                var ja = state.UserOverrides.TryGetValue(kv.Key, out var ov) ? ov : kv.Value;
                sw.WriteLine(string.Join(',', new[] { pluginName, loc?.Menu ?? string.Empty, loc?.Section ?? string.Empty, string.Empty, kv.Key, ja }.Select(CsvEscape)));
            }
            state.LastCsvPath = fullPath;
            SaveConfig();
            return $"CSVを書き出しました: {fullPath}";
        }
        catch (Exception ex) { return $"CSV書き出し失敗: {ex.Message}"; }
    }

    private string ImportDictionaryCsv(string pluginName) => ImportDictionaryCsv(pluginName, CsvPath(pluginName));

    private string ImportDictionaryCsv(string pluginName, string path)
    {
        try
        {
            if (!File.Exists(path)) return $"CSVがありません: {path}";
            var csvText = File.ReadAllText(path, Encoding.UTF8);
            var rows = ParseCsvRecords(csvText);
            var state = config.Plugins[pluginName];
            var imported = 0;
            foreach (var cols in rows.Skip(1))
            {
                if (cols.Count < 6) continue;
                var english = cols[4];
                var japanese = cols[5];
                if (string.IsNullOrWhiteSpace(english)) continue;
                if (string.IsNullOrWhiteSpace(japanese)) state.UserOverrides.Remove(english);
                else state.UserOverrides[english] = japanese;
                if (!string.IsNullOrWhiteSpace(cols[1]) || !string.IsNullOrWhiteSpace(cols[2]))
                    state.Locations[english] = new() { Menu = cols[1], Section = cols[2] };
                imported++;
            }
            state.LastCsvPath = Path.GetFullPath(path);
            editBuffers.Clear();
            SaveConfig();
            return $"CSVを読み込みました: {imported}件 / {state.LastCsvPath}";
        }
        catch (Exception ex) { return $"CSV読込失敗: {ex.Message}"; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExt;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    private string? ShowCsvOpenDialog()
    {
        const int maxChars = 32768;
        var buffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
        try
        {
            // Unicode NUL で初期化。
            for (var i = 0; i < maxChars; i++) Marshal.WriteInt16(buffer, i * sizeof(char), 0);
            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = IntPtr.Zero,
                Filter = "CSVファイル (*.csv)\0*.csv\0すべてのファイル (*.*)\0*.*\0\0",
                FilterIndex = 1,
                File = buffer,
                MaxFile = maxChars,
                InitialDir = DictionaryDirectory(),
                Title = $"{selectedPlugin} に読み込むCSVを選択",
                DefaultExt = "csv",
                // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
                Flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008,
            };
            if (!GetOpenFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex)
        {
            csvStatus = $"CSV選択画面を開けませんでした: {ex.Message}";
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string? ShowCsvSaveDialog(string pluginName, string? currentPath)
    {
        const int maxChars = 32768;
        var buffer = Marshal.AllocHGlobal(maxChars * sizeof(char));
        try
        {
            for (var i = 0; i < maxChars; i++) Marshal.WriteInt16(buffer, i * sizeof(char), 0);
            var initialName = !string.IsNullOrWhiteSpace(currentPath) ? Path.GetFileName(currentPath) : $"{pluginName}.csv";
            var chars = (initialName + "\0").ToCharArray();
            Marshal.Copy(chars, 0, buffer, Math.Min(chars.Length, maxChars));

            var initialDir = !string.IsNullOrWhiteSpace(currentPath) ? Path.GetDirectoryName(currentPath) : null;
            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir)) initialDir = DictionaryDirectory();

            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = IntPtr.Zero,
                Filter = "CSVファイル (*.csv)\0*.csv\0すべてのファイル (*.*)\0*.*\0\0",
                FilterIndex = 1,
                File = buffer,
                MaxFile = maxChars,
                InitialDir = initialDir,
                Title = $"{pluginName} の翻訳CSVを名前を付けて保存",
                DefaultExt = "csv",
                Flags = 0x00080000 | 0x00000800 | 0x00000008 | 0x00000002,
            };
            if (!GetSaveFileName(ref ofn)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        catch (Exception ex)
        {
            csvStatus = $"CSV保存画面を開けませんでした: {ex.Message}";
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string CsvEscape(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private static List<List<string>> ParseCsvRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();

                if (row.Any(x => x.Length > 0))
                    records.Add(row);

                row = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(x => x.Length > 0))
                records.Add(row);
        }

        return records;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else
            {
                if (c == '"') quoted = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private void SaveConfig()
    {
        InvalidateDictionaryUiCache();
        pluginInterface.SavePluginConfig(config);
    }

    public void Dispose()
    {
        captureEnabled = false; baselineCaptureEnabled = false;
        pluginInterface.UiBuilder.Draw -= Draw; pluginInterface.UiBuilder.OpenConfigUi -= OpenUi; pluginInterface.UiBuilder.OpenMainUi -= OpenUi; commandManager.RemoveHandler(Command);
        beginMenuHook?.Dispose(); menuItemBoolPtrHook?.Dispose(); menuItemBoolHook?.Dispose(); beginTabItemHook?.Dispose(); drawListAddTextVec2Hook?.Dispose(); collapsingHeaderBoolPtrHook?.Dispose(); collapsingHeaderTreeNodeFlagsHook?.Dispose(); renderTextClippedHook?.Dispose(); renderTextWrappedHook?.Dispose(); renderTextHook?.Dispose(); bulletTextHook?.Dispose(); textWrappedHook?.Dispose(); treeNodeExStrHook?.Dispose(); treeNodeStrHook?.Dispose(); radioButtonIntPtrHook?.Dispose(); radioButtonBoolHook?.Dispose(); endHook?.Dispose(); beginHook?.Dispose(); separatorTextHook?.Dispose(); endComboHook?.Dispose(); beginComboHook?.Dispose(); comboFnStrPtrHook?.Dispose(); comboStrHook?.Dispose(); comboStrArrHook?.Dispose(); selectablePtrHook?.Dispose(); selectableHook?.Dispose(); buttonHook?.Dispose(); checkboxHook?.Dispose(); textHook?.Dispose();
    }

    private sealed record CapturedItem(string Text, string Kind, int Count, string Menu, string Section, string Window);
}
