using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Penumbra.Api.Enums;

namespace EmotePlugin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly EmoteManager emoteManager;
    private readonly PenumbraService penumbraService;
    private readonly EmoteIconHelper emoteIconHelper;

    private string searchQuery = string.Empty;
    private EmoteEntry? selectedEmote;
    private EmoteFolder? selectedFolder;
    private readonly HashSet<Guid> selectedEmoteIds = new();
    private EmoteEntry? lastClickedEmote;
    private string newEmotePopupName = string.Empty;
    private string newFolderPopupName = string.Empty;
    private bool isRenaming;
    private string renameBuffer = string.Empty;
    private float sidebarWidth = 260f;

    // Folder rename state
    private Guid? renamingFolderId;
    private int renamingFrameCounter;

    // Cached Penumbra data
    private Dictionary<string, string> cachedMods = new();
    private Dictionary<Guid, string> cachedCollections = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, (string[] Options, GroupType Type)>?> cachedModOptions = new();
    private readonly Dictionary<string, HashSet<string>> cachedModEmoteCommands = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? cachedEmoteSlotIndex;
    private readonly Dictionary<(Guid Collection, string Dir), (bool Enabled, int Priority)> cachedModEnabledState = new();
    // Once per session per (emote, mod): re-clearing on the timer would overwrite
    // a user's deliberately emptied ("all mod defaults") settings.
    private readonly HashSet<(Guid EmoteId, string Dir)> settingsSyncAttempted = new();
    private long lastCacheRefresh;
    private long lastSlotIndexRefresh;

    // Conflict results cached across frames; recomputed on emote switch, cache refresh, or config change
    private Guid conflictCacheEmoteId;
    private long conflictCacheTick;
    private int conflictCacheRevision = -1;
    private Dictionary<string, (string Text, bool Winning)> cachedConflicts = new();

    // Mod picker state
    private string modSearchQuery = string.Empty;
    private string addModSearchQuery = string.Empty;

    // Drag-and-drop state
    private EmoteEntry? draggedEmote;
    private EmoteFolder? draggedFolder;

    // Tree line state: header midpoint captured in DrawFolderNode
    private float lastFolderHeaderMidY;

    private bool focusSettingsTab;

    // Command auto-detection state
    private (Guid EmoteId, string Message)? detectStatus;

    // Sidebar search results cached across frames
    private string cachedSearchQuery = string.Empty;
    private int cachedSearchRevision = -1;
    private List<EmoteEntry> cachedSearchResults = new();

    // Preview-timeline lookups cached across frames (string allocs per row otherwise)
    private readonly Dictionary<(string Command, int Pose), ushort?> previewTimelineCache = new();
    private int previewTimelineCacheRevision = -1;

    private ushort? GetPreviewTimelineCached(EmoteCommandEntry cmd)
    {
        if (previewTimelineCacheRevision != plugin.Configuration.Revision)
        {
            previewTimelineCache.Clear();
            previewTimelineCacheRevision = plugin.Configuration.Revision;
        }

        var key = (cmd.Command, cmd.PoseIndex);
        if (!previewTimelineCache.TryGetValue(key, out var timeline))
        {
            timeline = emoteIconHelper.GetPreviewTimeline(cmd.Command, cmd.PoseIndex);
            previewTimelineCache[key] = timeline;
        }

        return timeline;
    }

    // Import/export state
    private readonly FileDialogManager fileDialog = new();
    // Mod scanner state
    private sealed class ScanCandidate
    {
        public required string ModDirectory;
        public required string ModName;
        public string EmoteName = string.Empty;
        public string CommandsTooltip = string.Empty;
        public bool Selected;
        public bool AlreadyUsed;
        public List<(string Command, string Name, int Pose)> Rows = new();
    }

    private List<ScanCandidate>? pendingScan;
    private bool scanPopupPending;
    private Guid scanTargetFolderId = Guid.Empty;
    private string scanTargetFolderLabel = "(Root)";

    private EmoteFolder? pendingImport;
    private readonly List<ImportModRow> pendingImportMods = new();
    private readonly List<(EmoteEntry Emote, string Path)> pendingImportEmotes = new();
    private readonly Dictionary<Guid, List<ImportModRow>> pendingRowsByEmote = new();
    private readonly HashSet<Guid> importSelectedEmotes = new();
    private static readonly List<ImportModRow> EmptyImportRows = new();
    private bool importPopupPending;
    private string importError = string.Empty;
    private string importModSearch = string.Empty;

    private sealed class ImportModRow
    {
        public required EmoteEntry Emote;
        public required ModAssociation Mod;
        public string MapDir = string.Empty;
        public string MapName = string.Empty;
        public bool Remove;
    }

    public MainWindow(Plugin plugin, EmoteManager emoteManager, PenumbraService penumbraService,
        EmoteIconHelper emoteIconHelper)
        : base("Emote Plugin###EmotePluginMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        this.emoteManager = emoteManager;
        this.penumbraService = penumbraService;
        this.emoteIconHelper = emoteIconHelper;
    }

    public void Dispose() { }

    /// <summary>
    /// Drawn from UiBuilder.Draw directly (not Window.Draw) so open file dialogs and
    /// the import/scan review modals survive the main window being closed mid-flow.
    /// </summary>
    public void DrawOverlays()
    {
        fileDialog.Draw();
        DrawImportPopup();
        DrawScanPopup();
    }

    private void RunModScan()
    {
        if (!penumbraService.Available)
        {
            importError = "Penumbra is not available.";
            return;
        }

        var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in emoteManager.GetRootFolder().EnumerateEmotes())
        foreach (var mod in existing.AssociatedMods)
            usedDirs.Add(mod.ModDirectory);

        var modRoot = penumbraService.GetModRootDirectory();
        var candidates = new List<ScanCandidate>();

        foreach (var (dir, itemNames) in penumbraService.GetAllChangedItemNames())
        {
            var detected = new Dictionary<(string Command, int Pose), string>();
            var hasPoseFilePaths = false;
            foreach (var itemName in itemNames)
            {
                var resolved = emoteIconHelper.ResolveEmote(itemName);
                if (resolved != null)
                    detected.TryAdd((resolved.Value.Command, 0), resolved.Value.Name);
                else if (!hasPoseFilePaths && PoseFileRegex.IsMatch(itemName))
                    hasPoseFilePaths = true; // unidentified changed items are raw game paths
            }

            if (detected.Count == 0 && !hasPoseFilePaths)
                continue; // no emote changes in this mod

            // Verify on disk that the mod actually replaces an emote animation (.pap),
            // not just VFX/sound tied to an emote. Without disk access we can't tell,
            // so fall back to including the mod.
            var hasEmoteAnimation = true;
            if (!string.IsNullOrEmpty(modRoot))
                hasEmoteAnimation = CollectPoseRowsFromDisk(modRoot, dir, detected);

            if (!hasEmoteAnimation || detected.Count == 0)
                continue;

            var name = cachedMods.TryGetValue(dir, out var n) && !string.IsNullOrWhiteSpace(n) ? n : dir;
            var alreadyUsed = usedDirs.Contains(dir);
            var rows = FinalizeDetectedRows(detected);
            candidates.Add(new ScanCandidate
            {
                ModDirectory = dir,
                ModName = name,
                EmoteName = name,
                Selected = !alreadyUsed,
                AlreadyUsed = alreadyUsed,
                Rows = rows,
                CommandsTooltip = string.Join("\n", rows.Select(
                    r => r.Pose > 0 ? $"{r.Command} (pose {r.Pose})" : r.Command)),
            });
        }

        if (candidates.Count == 0)
        {
            importError = "No mods with emote animations found.";
            return;
        }

        candidates.Sort((a, b) => string.Compare(a.ModName, b.ModName, StringComparison.OrdinalIgnoreCase));
        pendingScan = candidates;
        scanTargetFolderId = Guid.Empty;
        scanTargetFolderLabel = "(Root)";
        scanPopupPending = true;
    }

    private void DrawScanPopup()
    {
        if (scanPopupPending)
        {
            ImGui.OpenPopup("Scan Mods###ScanModsPopup");
            scanPopupPending = false;
        }

        if (pendingScan == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(760, 500), ImGuiCond.FirstUseEver);
        var open = true;
        if (!ImGui.BeginPopupModal("Scan Mods###ScanModsPopup", ref open))
        {
            pendingScan = null; // dismissed (Escape)
            return;
        }

        var selectedCount = pendingScan.Count(c => c.Selected);
        ImGui.Text($"Found {pendingScan.Count} mod(s) with emote animations — importing {selectedCount} as new emotes.");
        ImGui.TextWrapped("Edit the emote names, untick mods you don't want. Mods already associated with an " +
                          "existing emote are unticked by default.");

        if (ImGui.SmallButton("Select All"))
        {
            foreach (var c in pendingScan)
                c.Selected = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Select None"))
        {
            foreach (var c in pendingScan)
                c.Selected = false;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        DrawScanFolderCombo();

        ImGui.Spacing();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var child = ImRaii.Child("##ScanReview", new Vector2(-1, -footerHeight), true))
        {
            if (child.Success)
                DrawScanTable();
        }

        ImGui.BeginDisabled(selectedCount == 0);
        if (ImGui.Button("Import", new Vector2(120, 0)))
        {
            ApplyScanImport();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            pendingScan = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        if (!open)
            pendingScan = null;
    }

    private void DrawScanFolderCombo()
    {
        if (!ImGui.BeginCombo("##ScanTargetFolder", $"Into: {scanTargetFolderLabel}"))
            return;

        if (ImGui.Selectable("(Root)", scanTargetFolderId == Guid.Empty))
        {
            scanTargetFolderId = Guid.Empty;
            scanTargetFolderLabel = "(Root)";
        }

        foreach (var (folder, path) in FoldersWithPaths(emoteManager.GetRootFolder(), string.Empty))
        {
            if (ImGui.Selectable($"{path}##{folder.Id}", folder.Id == scanTargetFolderId))
            {
                scanTargetFolderId = folder.Id;
                scanTargetFolderLabel = path;
            }
        }

        ImGui.EndCombo();
    }

    /// <summary> Folders with their full path labels, so same-named folders stay distinguishable. </summary>
    private static IEnumerable<(EmoteFolder Folder, string Path)> FoldersWithPaths(EmoteFolder folder, string prefix)
    {
        foreach (var sub in folder.Folders)
        {
            var path = prefix.Length == 0 ? sub.Name : $"{prefix}/{sub.Name}";
            yield return (sub, path);
            foreach (var nested in FoldersWithPaths(sub, path))
                yield return nested;
        }
    }

    private void DrawScanTable()
    {
        if (pendingScan == null || !ImGui.BeginTable("##ScanTable", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##Sel", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 4);
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Emote Name", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("Commands", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableHeadersRow();

        for (var i = 0; i < pendingScan.Count; i++)
        {
            var candidate = pendingScan[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            ImGui.TableNextColumn();
            var selected = candidate.Selected;
            if (ImGui.Checkbox("##scanSel", ref selected))
                candidate.Selected = selected;

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(candidate.ModName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(candidate.ModDirectory);

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!candidate.Selected);
            var emoteName = candidate.EmoteName;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##scanName", ref emoteName, 128))
                candidate.EmoteName = emoteName;
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"{candidate.Rows.Count}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(candidate.CommandsTooltip);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (candidate.AlreadyUsed)
            {
                ImGui.TextDisabled("Exists");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This mod is already associated with an existing emote.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.35f, 1f), "New");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void ApplyScanImport()
    {
        if (pendingScan == null)
            return;

        var root = emoteManager.GetRootFolder();
        var target = scanTargetFolderId == Guid.Empty
            ? root
            : root.SelfAndDescendants().FirstOrDefault(f => f.Id == scanTargetFolderId) ?? root;

        foreach (var candidate in pendingScan.Where(c => c.Selected))
        {
            var emote = new EmoteEntry
            {
                Name = string.IsNullOrWhiteSpace(candidate.EmoteName) ? candidate.ModName : candidate.EmoteName,
            };

            var mod = new ModAssociation
            {
                ModDirectory = candidate.ModDirectory,
                ModName = candidate.ModName,
                Enabled = true,
                Priority = 1,
            };
            emote.AssociatedMods.Add(mod);

            foreach (var (command, name, pose) in candidate.Rows)
                emote.Commands.Add(new EmoteCommandEntry { Name = name, Command = command, PoseIndex = pose });

            emote.DefaultCommandId = emote.Commands.FirstOrDefault()?.Id ?? Guid.Empty;

            emoteManager.AddEmote(emote, target);
            emoteManager.SyncModSettingsFromPenumbra(emote, mod);
        }

        pendingScan = null;
    }

    public override void Draw()
    {
        RefreshCacheIfNeeded();

        using var tabs = ImRaii.TabBar("##MainTabs");
        if (!tabs.Success) return;

        using (var emotesTab = ImRaii.TabItem("Emotes"))
        {
            if (emotesTab.Success)
                DrawEmotesTab();
        }

        var settingsFlags = focusSettingsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        focusSettingsTab = false;
        using (var settingsTab = ImRaii.TabItem("Settings", settingsFlags))
        {
            if (settingsTab.Success)
                DrawPluginSettingsTab();
        }
    }

    public void ToggleSettings()
    {
        // Never close here: the config-UI hook must surface settings, not
        // dismiss a window the user is working in.
        IsOpen = true;
        focusSettingsTab = true;
    }

    private void DrawEmotesTab()
    {
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var splitterWidth = Math.Max(4f, ImGuiHelpers.GlobalScale * 4f);
        var minSidebarWidth = 180f;
        var minSettingsWidth = 240f;
        var maxSidebarWidth = Math.Max(minSidebarWidth, availableWidth - minSettingsWidth - splitterWidth);
        sidebarWidth = float.Clamp(sidebarWidth, minSidebarWidth, maxSidebarWidth);

        // Left panel: Emote list
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (var child = ImRaii.Child("##EmoteSidebar", new Vector2(sidebarWidth, availableHeight), true))
        {
            if (child.Success)
                DrawSidebar();
        }

        ImGui.SameLine(0f, 0f);

        var splitterScreenPos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##SidebarSplitter", new Vector2(splitterWidth, availableHeight));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor((ImGuiMouseCursor)4);
        if (ImGui.IsItemActive())
            sidebarWidth = float.Clamp(sidebarWidth + ImGui.GetIO().MouseDelta.X, minSidebarWidth, maxSidebarWidth);

        var splitterColor = ImGui.GetColorU32(ImGui.IsItemActive()
            ? ImGuiCol.SeparatorActive
            : ImGui.IsItemHovered() ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
        ImGui.GetWindowDrawList().AddRectFilled(splitterScreenPos,
            splitterScreenPos + new Vector2(splitterWidth, availableHeight), splitterColor);

        ImGui.SameLine(0f, 0f);

        // Right panel: Emote settings
        using (var child = ImRaii.Child("##EmoteSettings", new Vector2(-1, -1), true))
        {
            if (child.Success)
                DrawSettingsPanel();
        }
    }

    private void DrawPluginSettingsTab()
    {
        ImGui.Text($"Emotes configured: {emoteManager.GetEmoteCount()}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showQuickAccess = plugin.Configuration.ShowQuickAccess;
        if (ImGui.Checkbox("Show Quick Access Widget", ref showQuickAccess))
        {
            plugin.Configuration.ShowQuickAccess = showQuickAccess;
            plugin.Configuration.Save();
            plugin.SetQuickAccessVisible(showQuickAccess);
        }

        var showSubCommands = plugin.Configuration.QuickAccessShowSubCommands;
        if (ImGui.Checkbox("List Sub-Commands in Quick Access", ref showSubCommands))
        {
            plugin.Configuration.QuickAccessShowSubCommands = showSubCommands;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When an emote has multiple enabled commands, list each one\nas its own entry in the Quick Access dropdown.");

        var alwaysRedraw = plugin.Configuration.AlwaysRedraw;
        if (ImGui.Checkbox("Always Redraw on Emote Use", ref alwaysRedraw))
        {
            plugin.Configuration.AlwaysRedraw = alwaysRedraw;
            plugin.Configuration.Save();
        }

        var sitDozeAnywhere = plugin.Configuration.SitDozeAnywhere;
        if (ImGui.Checkbox("Sit/Doze Anywhere", ref sitDozeAnywhere))
        {
            plugin.Configuration.SitDozeAnywhere = sitDozeAnywhere;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Play /sit and /doze commands using the game's hidden furniture emote,\nso they work without a chair or bed nearby.");

        if (sitDozeAnywhere)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Other players see this non-standard behavior (sitting in mid-air,\ndozing on bare ground). Use with discretion in public areas.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Import / Export");

        if (ImGui.Button("Export Emotes..."))
        {
            importError = string.Empty;
            fileDialog.SaveFileDialog("Export Emotes", ".json", "emotes.json", ".json", (ok, path) =>
            {
                if (!ok) return;
                try
                {
                    File.WriteAllText(path, emoteManager.ExportToJson());
                }
                catch (Exception ex)
                {
                    importError = $"Export failed: {ex.Message}";
                }
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Import Emotes..."))
        {
            importError = string.Empty;
            fileDialog.OpenFileDialog("Import Emotes", ".json", (ok, path) =>
            {
                if (!ok) return;
                try
                {
                    var root = EmoteManager.ParseImport(File.ReadAllText(path));
                    if (root == null || CountEmotes(root) == 0)
                    {
                        importError = "No emotes found in the selected file.";
                        return;
                    }

                    pendingImport = root;
                    pendingImportMods.Clear();
                    pendingImportEmotes.Clear();
                    pendingRowsByEmote.Clear();
                    CollectImportEntries(root, string.Empty);
                    importSelectedEmotes.Clear();
                    foreach (var (importEmote, _) in pendingImportEmotes)
                        importSelectedEmotes.Add(importEmote.Id);
                    importModSearch = string.Empty;
                    importPopupPending = true;
                    lastCacheRefresh = 0; // refresh mod/collection lists for the review dialog
                }
                catch (Exception ex)
                {
                    importError = $"Import failed: {ex.Message}";
                }
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Scan Mods for Animations..."))
        {
            importError = string.Empty;
            RunModScan();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan every installed Penumbra mod for emote animations and\nturn the matches into emotes (with a review step).");

        if (!string.IsNullOrEmpty(importError))
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), importError);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("What's New..."))
            plugin.ShowWhatsNew();
    }

    private static int CountEmotes(EmoteFolder folder)
        => folder.Emotes.Count + folder.Folders.Sum(CountEmotes);

    private void CollectImportEntries(EmoteFolder folder, string path)
    {
        foreach (var emote in folder.Emotes)
        {
            pendingImportEmotes.Add((emote, path));
            foreach (var mod in emote.AssociatedMods)
            {
                var row = new ImportModRow
                {
                    Emote = emote,
                    Mod = mod,
                    MapDir = mod.ModDirectory ?? string.Empty,
                    MapName = mod.ModName ?? string.Empty,
                };
                pendingImportMods.Add(row);

                if (!pendingRowsByEmote.TryGetValue(emote.Id, out var list))
                    pendingRowsByEmote[emote.Id] = list = new List<ImportModRow>();
                list.Add(row);
            }
        }

        foreach (var sub in folder.Folders)
            CollectImportEntries(sub, path.Length == 0 ? sub.Name : $"{path}/{sub.Name}");
    }

    private void DrawImportPopup()
    {
        if (importPopupPending)
        {
            ImGui.OpenPopup("Import Emotes###ImportEmotesPopup");
            importPopupPending = false;
        }

        if (pendingImport == null)
            return;

        ImGui.SetNextWindowSize(new Vector2(700, 450), ImGuiCond.FirstUseEver);
        var open = true;
        if (!ImGui.BeginPopupModal("Import Emotes###ImportEmotesPopup", ref open))
        {
            // The popup should be open whenever an import is pending; if it isn't,
            // it was dismissed (Escape closes modals without writing `open`) —
            // discard the pending import instead of leaving it orphaned.
            ClearPendingImport();
            return;
        }

        ImGui.Text($"Importing {importSelectedEmotes.Count} of {pendingImportEmotes.Count} emote(s). Same-named folders are merged.");
        ImGui.TextWrapped("Untick emotes you don't want. Mods missing from your Penumbra installation are " +
                          "highlighted — remap them to an installed mod, keep them as-is, or drop them.");

        if (ImGui.SmallButton("Select All"))
        {
            foreach (var (importEmote, _) in pendingImportEmotes)
                importSelectedEmotes.Add(importEmote.Id);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Select None"))
            importSelectedEmotes.Clear();

        ImGui.Spacing();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var child = ImRaii.Child("##ImportReview", new Vector2(-1, -footerHeight), true))
        {
            if (child.Success)
                DrawImportTable();
        }

        ImGui.BeginDisabled(importSelectedEmotes.Count == 0);
        if (ImGui.Button("Import", new Vector2(120, 0)))
        {
            ApplyImport();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
        {
            ClearPendingImport();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        if (!open)
            ClearPendingImport();
    }

    private void ClearPendingImport()
    {
        pendingImport = null;
        pendingImportMods.Clear();
        pendingImportEmotes.Clear();
        pendingRowsByEmote.Clear();
        importSelectedEmotes.Clear();
    }

    private void DrawImportTable()
    {
        if (!ImGui.BeginTable("##ImportTable", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            return;

        ImGui.TableSetupColumn("##Sel", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() + 4);
        ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableHeadersRow();

        var rowId = 0;
        foreach (var (emote, path) in pendingImportEmotes)
        {
            var rows = pendingRowsByEmote.TryGetValue(emote.Id, out var list) ? list : EmptyImportRows;
            var selected = importSelectedEmotes.Contains(emote.Id);

            ImGui.TableNextRow();
            ImGui.PushID(rowId++);

            // Include checkbox
            ImGui.TableNextColumn();
            if (ImGui.Checkbox("##sel", ref selected))
            {
                if (selected)
                    importSelectedEmotes.Add(emote.Id);
                else
                    importSelectedEmotes.Remove(emote.Id);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Import this emote");

            // Emote name with dimmed folder path
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (path.Length > 0)
            {
                ImGui.TextDisabled(path + "/");
                ImGui.SameLine(0, 0);
            }
            ImGui.Text(emote.Name.Length == 0 ? "(unnamed)" : emote.Name);

            ImGui.BeginDisabled(!selected);
            if (rows.Count == 0)
            {
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled("(no mods)");
            }
            else
            {
                DrawImportModCells(rows[0]);
            }
            ImGui.EndDisabled();
            ImGui.PopID();

            // One extra row per additional associated mod
            for (var i = 1; i < rows.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.PushID(rowId++);
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.BeginDisabled(!selected);
                DrawImportModCells(rows[i]);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }

        ImGui.EndTable();
    }

    private void DrawImportModCells(ImportModRow row)
    {
        var mappedFound = cachedMods.ContainsKey(row.MapDir);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (row.Remove)
            ImGui.TextDisabled("Dropped");
        else if (!penumbraService.Available)
            ImGui.TextColored(new Vector4(1, 1, 0.4f, 1), "Unknown");
        else if (mappedFound)
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Found");
        else
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Missing");

        ImGui.TableNextColumn();
        var preview = row.Remove
            ? "(Don't import this mod)"
            : string.IsNullOrWhiteSpace(row.MapName) ? row.MapDir : row.MapName;

        ImGui.SetNextItemWidth(-1);
        var comboWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextWindowSizeConstraints(new Vector2(comboWidth, 0), new Vector2(comboWidth, 400));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginCombo("##ImportModSelect", preview))
        {
            // Filter pinned above the scrolling list — flush and square (Glamourer-style)
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##ImportModSearch", "Search mods...", ref importModSearch, 128);
            ImGui.PopStyleVar();

            if (ImGui.BeginChild("##ImportModItems", new Vector2(-1, 320)))
            {
                var originalLabel = row.Mod.DisplayName;
                if (ImGui.Selectable($"(Keep original: {originalLabel})",
                        !row.Remove && string.Equals(row.MapDir, row.Mod.ModDirectory, StringComparison.OrdinalIgnoreCase)))
                {
                    row.MapDir = row.Mod.ModDirectory;
                    row.MapName = row.Mod.ModName;
                    row.Remove = false;
                    ImGui.CloseCurrentPopup(); // child-hosted selectables don't auto-close
                }

                if (ImGui.Selectable("(Don't import this mod)", row.Remove))
                {
                    row.Remove = true;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.Separator();

                foreach (var (dir, name) in cachedMods)
                {
                    if (!string.IsNullOrWhiteSpace(importModSearch) &&
                        !dir.Contains(importModSearch, StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(importModSearch, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var label = string.IsNullOrWhiteSpace(name) ? dir : name;
                    if (ImGui.Selectable($"{label}##{dir}",
                            !row.Remove && string.Equals(row.MapDir, dir, StringComparison.OrdinalIgnoreCase)))
                    {
                        row.MapDir = dir;
                        row.MapName = name;
                        row.Remove = false;
                        importModSearch = string.Empty;
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.EndChild();
            ImGui.EndCombo();
        }

        ImGui.PopStyleVar();
    }

    private void ApplyImport()
    {
        if (pendingImport == null)
            return;

        PruneUnselectedEmotes(pendingImport);

        foreach (var row in pendingImportMods)
        {
            if (!importSelectedEmotes.Contains(row.Emote.Id))
                continue;

            if (row.Remove)
            {
                row.Emote.AssociatedMods.Remove(row.Mod);
            }
            else
            {
                // Remapped to a different mod: the stored option settings belong to the
                // old mod's groups and would make Penumbra reject the whole apply call.
                if (!string.Equals(row.MapDir, row.Mod.ModDirectory, StringComparison.OrdinalIgnoreCase))
                    row.Mod.Settings = new Dictionary<string, List<string>>();

                row.Mod.ModDirectory = row.MapDir;
                row.Mod.ModName = row.MapName;
            }
        }

        // Remapping can leave one emote with two associations for the same mod — keep the first
        foreach (var emote in pendingImport.EnumerateEmotes())
        {
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            emote.AssociatedMods.RemoveAll(m => !seenDirs.Add(m.ModDirectory));
        }

        // Only validate collections against a real collection list; with Penumbra
        // unavailable the cache is empty and resetting would wipe valid assignments.
        if (penumbraService.Available && cachedCollections.Count > 0)
            ResetUnknownCollections(pendingImport);

        emoteManager.ImportEmotes(pendingImport);

        ClearPendingImport();
    }

    private void PruneUnselectedEmotes(EmoteFolder folder)
    {
        folder.Emotes.RemoveAll(e => !importSelectedEmotes.Contains(e.Id));
        foreach (var sub in folder.Folders)
            PruneUnselectedEmotes(sub);

        // Drop folders that end up with nothing to import
        folder.Folders.RemoveAll(f => !f.EnumerateEmotes().Any());
    }

    private void ResetUnknownCollections(EmoteFolder folder)
    {
        foreach (var emote in folder.EnumerateEmotes())
        {
            if (emote.PenumbraCollectionId != Guid.Empty && !cachedCollections.ContainsKey(emote.PenumbraCollectionId))
                emote.PenumbraCollectionId = Guid.Empty;
        }
    }

    private void DrawSidebar()
    {
        // Search bar
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 0f))
            ImGui.InputTextWithHint("##EmoteSearch", "Search emotes...", ref searchQuery, 128);

        // Scrollable tree area (reserve space for bottom toolbar)
        var toolbarHeight = ImGui.GetFrameHeight();
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (var child = ImRaii.Child("##EmoteTree", new Vector2(-1, -toolbarHeight)))
        {
            if (child.Success)
            {
                var scale = ImGuiHelpers.GlobalScale;
                using var style = ImRaii.PushStyle(ImGuiStyleVar.IndentSpacing, 14f * scale)
                    .Push(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, scale))
                    .Push(ImGuiStyleVar.FramePadding, new Vector2(scale, ImGui.GetStyle().FramePadding.Y));
                if (string.IsNullOrWhiteSpace(searchQuery))
                    DrawFolderContents(emoteManager.GetRootFolder());
                else
                    DrawFilteredEmotes();

                // Drop target on empty area to move folders/emotes back to root
                ImGui.Dummy(ImGui.GetContentRegionAvail());
                if (ImGui.BeginDragDropTarget())
                {
                    var root = emoteManager.GetRootFolder();
                    var folderPayload = ImGui.AcceptDragDropPayload("FOLDER_ITEM");
                    if (!folderPayload.IsNull && draggedFolder != null)
                    {
                        emoteManager.MoveFolderToFolder(draggedFolder, root);
                        draggedFolder = null;
                    }

                    var emotePayload = ImGui.AcceptDragDropPayload("EMOTE_ITEM");
                    if (!emotePayload.IsNull && draggedEmote != null)
                    {
                        emoteManager.MoveEmoteToFolderAt(draggedEmote, root, root.Emotes.Count);
                        draggedEmote = null;
                    }

                    ImGui.EndDragDropTarget();
                }
            }
        }

        DrawSidebarToolbar();
    }

    private const uint FolderLineColor = 0xFFFFFFFF;
    private static readonly Vector4 RedButtonColor = new(0.7f, 0.2f, 0.2f, 1f);
    private static readonly Vector4 RedButtonActiveColor = new(0.6f, 0.15f, 0.15f, 1f);
    private static readonly Vector2 ContextMenuWindowPadding = new(4f, 4f);
    private static readonly Vector2 ContextMenuFramePadding = new(4f, 2f);
    private static readonly Vector2 ContextMenuItemSpacing = new(4f, 1f);

    private List<EmoteEntry> GetFlatEmoteList()
    {
        var result = new List<EmoteEntry>();
        CollectEmotes(emoteManager.GetRootFolder(), result);
        return result;
    }

    private void SelectFolder(EmoteFolder folder)
    {
        selectedFolder = folder;
        selectedEmote = null;
        selectedEmoteIds.Clear();
        lastClickedEmote = null;
        isRenaming = false;
    }

    private void SelectSingleEmote(EmoteEntry emote)
    {
        selectedFolder = null;
        selectedEmoteIds.Clear();
        selectedEmoteIds.Add(emote.Id);
        selectedEmote = emote;
        lastClickedEmote = emote;
        isRenaming = false;
    }

    private void ClearSelection()
    {
        selectedFolder = null;
        selectedEmote = null;
        selectedEmoteIds.Clear();
        lastClickedEmote = null;
        isRenaming = false;
    }

    private void SyncSelectedEmoteFromIds()
        => selectedEmote = selectedEmoteIds.Count > 0
            ? emoteManager.GetAllEmotes().FirstOrDefault(e => selectedEmoteIds.Contains(e.Id))
            : null;

    private void CollectEmotes(EmoteFolder folder, List<EmoteEntry> result)
    {
        foreach (var sub in folder.Folders)
            CollectEmotes(sub, result);
        result.AddRange(folder.Emotes);
    }

    private void DeleteSelectedEmotes()
    {
        var allEmotes = emoteManager.GetAllEmotes();
        var toDelete = allEmotes.Where(e => selectedEmoteIds.Contains(e.Id)).ToList();
        foreach (var e in toDelete)
            emoteManager.RemoveEmote(e);
        ClearSelection();
    }

    private void StartFolderRename(EmoteFolder folder)
    {
        renamingFolderId = folder.Id;
        renameBuffer = folder.Name;
        renamingFrameCounter = 2;
    }

    private static void DrawContextMenu(string popupId, Action drawContents)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, ContextMenuWindowPadding)
            .Push(ImGuiStyleVar.FramePadding, ContextMenuFramePadding)
            .Push(ImGuiStyleVar.ItemSpacing, ContextMenuItemSpacing);
        if (!ImGui.BeginPopupContextItem(popupId))
            return;

        drawContents();
        ImGui.EndPopup();
    }

    private void DrawFolderContents(EmoteFolder folder)
    {
        for (var i = 0; i < folder.Folders.Count; i++)
            DrawFolderNode(folder.Folders[i]);

        for (var i = 0; i < folder.Emotes.Count; i++)
            DrawEmoteItem(folder.Emotes[i], folder);

        if (folder == emoteManager.GetRootFolder() && folder.Folders.Count == 0 && folder.Emotes.Count == 0)
            ImGui.TextDisabled("No emotes. Click + to add one.");
    }

    private void DrawChildrenWithLines(EmoteFolder folder)
    {
        var childCount = folder.Folders.Count + folder.Emotes.Count;
        if (childCount == 0)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var indentSpacing = ImGui.GetStyle().IndentSpacing;
        var lineStart = ImGui.GetCursorScreenPos();
        lineStart.X += -indentSpacing + ImGui.GetTreeNodeToLabelSpacing() / 2f;
        lineStart.Y -= 2f * scale;
        var lineEnd = lineStart;
        var lineSize = Math.Max(0, indentSpacing - 9f * scale);
        var lineThickness = 2f * scale;

        foreach (var sub in folder.Folders)
        {
            DrawFolderNode(sub);
            var midPoint = lastFolderHeaderMidY;
            drawList.AddLine(
                new Vector2(lineStart.X, midPoint),
                new Vector2(lineStart.X + lineSize, midPoint),
                FolderLineColor, lineThickness);
            lineEnd.Y = midPoint;
        }

        foreach (var emote in folder.Emotes)
        {
            DrawEmoteItem(emote, folder);
            var minRect = ImGui.GetItemRectMin();
            var maxRect = ImGui.GetItemRectMax();
            if (minRect.X == 0) continue;
            var midPoint = (minRect.Y + maxRect.Y) / 2f - 1f;
            drawList.AddLine(
                new Vector2(lineStart.X, midPoint),
                new Vector2(lineStart.X + lineSize, midPoint),
                FolderLineColor, lineThickness);
            lineEnd.Y = midPoint;
        }

        // Vertical line from top to last child's midpoint
        if (lineEnd.Y > lineStart.Y)
            drawList.AddLine(lineStart, lineEnd, FolderLineColor, lineThickness);
    }

    private void DrawFolderNode(EmoteFolder folder)
    {
        // Inline rename mode
        if (renamingFolderId == folder.Id)
        {
            if (renamingFrameCounter > 0)
            {
                ImGui.SetKeyboardFocusHere();
                renamingFrameCounter--;
            }
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.InputText($"##renFolder_{folder.Id}", ref renameBuffer, 128, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (!string.IsNullOrWhiteSpace(renameBuffer))
                    emoteManager.RenameFolder(folder, renameBuffer);
                renamingFolderId = null;
            }
            else if (renamingFrameCounter == 0 && !ImGui.IsItemActive())
            {
                renamingFolderId = null;
            }
            return;
        }

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth
                  | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (selectedFolder?.Id == folder.Id)
            flags |= ImGuiTreeNodeFlags.Selected;
        var open = ImGui.TreeNodeEx($"{folder.Name}##folder_{folder.Id}", flags);

        // Capture header rect for tree line drawing
        var headerMin = ImGui.GetItemRectMin();
        var headerMax = ImGui.GetItemRectMax();
        lastFolderHeaderMidY = (headerMin.Y + headerMax.Y) / 2f - 1f;

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            SelectFolder(folder);

        // DnD source: drag folder
        if (ImGui.BeginDragDropSource())
        {
            draggedFolder = folder;
            draggedEmote = null;
            ImGui.SetDragDropPayload("FOLDER_ITEM", new byte[] { 1 });
            ImGui.Text(folder.Name);
            ImGui.EndDragDropSource();
        }

        // DnD target: accept emotes or folders dropped onto this folder
        if (ImGui.BeginDragDropTarget())
        {
            var emotePayload = ImGui.AcceptDragDropPayload("EMOTE_ITEM");
            if (!emotePayload.IsNull && draggedEmote != null)
            {
                emoteManager.MoveEmoteToFolderAt(draggedEmote, folder, folder.Emotes.Count);
                draggedEmote = null;
            }

            var folderPayload = ImGui.AcceptDragDropPayload("FOLDER_ITEM");
            if (!folderPayload.IsNull && draggedFolder != null && draggedFolder.Id != folder.Id)
            {
                emoteManager.MoveFolderToFolder(draggedFolder, folder);
                draggedFolder = null;
            }

            ImGui.EndDragDropTarget();
        }

        // Context menu
        DrawContextMenu($"##folder_ctx_{folder.Id}", () =>
        {
            if (ImGui.MenuItem("Rename"))
                StartFolderRename(folder);

            var parentOfFolder = emoteManager.FindParentOfFolder(folder);
            if (parentOfFolder != null && parentOfFolder != emoteManager.GetRootFolder())
            {
                var grandparent = emoteManager.FindParentOfFolder(parentOfFolder);
                if (grandparent != null && ImGui.MenuItem("Move to Parent"))
                {
                    emoteManager.MoveFolderToFolder(folder, grandparent);
                }
            }
            else if (parentOfFolder != null && parentOfFolder == emoteManager.GetRootFolder())
            {
                // Already at root — disabled hint
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Delete Folder"))
            {
                emoteManager.RemoveFolder(folder);
                if (selectedFolder?.Id == folder.Id)
                    ClearSelection();
            }
        });

        if (open)
        {
            ImGui.Indent();
            DrawChildrenWithLines(folder);
            ImGui.Unindent();
        }
    }

    private void DrawEmoteItem(EmoteEntry emote, EmoteFolder? parentFolder)
    {
        var isSelected = selectedEmoteIds.Contains(emote.Id);
        var hasModAssociation = emote.AssociatedMods.Count > 0;

        // Determine text color: green if has active mods, dimmed if no mods or all disabled
        var anyEnabled = false;
        if (hasModAssociation)
        {
            foreach (var mod in emote.AssociatedMods)
            {
                if (mod.Enabled) { anyEnabled = true; break; }
            }
        }

        var textColor = anyEnabled
            ? new Vector4(0.4f, 0.9f, 0.4f, 1.0f)
            : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

        using (ImRaii.PushColor(ImGuiCol.Text, textColor))
        {
            var leafFlags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet
                          | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (isSelected)
                leafFlags |= ImGuiTreeNodeFlags.Selected;

            ImGui.TreeNodeEx($"{emote.Name}##emote_{emote.Id}", leafFlags);
        }

        // Click to select (Ctrl = toggle, Shift = range, plain = single)
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            var io = ImGui.GetIO();
            if (io.KeyCtrl)
            {
                // Toggle this emote in selection
                if (selectedEmoteIds.Contains(emote.Id))
                {
                    selectedEmoteIds.Remove(emote.Id);
                    if (selectedEmote?.Id == emote.Id)
                        SyncSelectedEmoteFromIds();
                }
                else
                {
                    selectedFolder = null;
                    selectedEmoteIds.Add(emote.Id);
                    selectedEmote = emote;
                }
            }
            else if (io.KeyShift && lastClickedEmote != null)
            {
                // Range select
                var flat = GetFlatEmoteList();
                var idxA = flat.FindIndex(e => e.Id == lastClickedEmote.Id);
                var idxB = flat.FindIndex(e => e.Id == emote.Id);
                if (idxA >= 0 && idxB >= 0)
                {
                    selectedFolder = null;
                    var from = Math.Min(idxA, idxB);
                    var to = Math.Max(idxA, idxB);
                    selectedEmoteIds.Clear();
                    for (var ri = from; ri <= to; ri++)
                        selectedEmoteIds.Add(flat[ri].Id);
                    selectedEmote = emote;
                }
            }
            else
            {
                // Single select
                SelectSingleEmote(emote);
            }

            lastClickedEmote = emote;
            isRenaming = false;
        }

        // Double-click to use emote
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            emoteManager.UseEmote(emote);

        // Drag-and-drop (only when not filtering and parent folder known)
        if (parentFolder != null && string.IsNullOrEmpty(searchQuery))
        {
            if (ImGui.BeginDragDropSource())
            {
                draggedEmote = emote;
                ImGui.SetDragDropPayload("EMOTE_ITEM", new byte[] { 1 });
                ImGui.Text(emote.Name);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("EMOTE_ITEM");
                if (!payload.IsNull && draggedEmote != null && draggedEmote.Id != emote.Id)
                {
                    var dstIndex = parentFolder.Emotes.IndexOf(emote);
                    emoteManager.MoveEmoteToFolderAt(draggedEmote, parentFolder, dstIndex);
                    draggedEmote = null;
                }
                ImGui.EndDragDropTarget();
            }
        }

        // Right-click context menu
        DrawContextMenu($"##emote_ctx_{emote.Id}", () =>
        {
            if (ImGui.MenuItem("Use Emote"))
                emoteManager.UseEmote(emote);

            if (ImGui.MenuItem("Duplicate"))
            {
                var dup = emoteManager.DuplicateEmote(emote);
                SelectSingleEmote(dup);
            }

            if (ImGui.MenuItem("Rename"))
            {
                SelectSingleEmote(emote);
                isRenaming = true;
                renameBuffer = emote.Name;
            }

            ImGui.Separator();

            if (hasModAssociation)
            {
                if (ImGui.MenuItem("Apply All Mods"))
                    emoteManager.ApplyAllModSettings(emote);
                if (ImGui.MenuItem("Disable All Mods"))
                    emoteManager.DisableEmoteMods(emote);

                ImGui.Separator();
            }

            if (parentFolder != null && parentFolder.Emotes.Count > 1)
            {
                var emoteIndex = parentFolder.Emotes.IndexOf(emote);
                if (emoteIndex > 0 && ImGui.MenuItem("Move Up"))
                    emoteManager.MoveEmote(emote, -1);
                if (emoteIndex < parentFolder.Emotes.Count - 1 && ImGui.MenuItem("Move Down"))
                    emoteManager.MoveEmote(emote, 1);

                ImGui.Separator();
            }

            if (ImGui.MenuItem("Delete"))
            {
                emoteManager.RemoveEmote(emote);
                selectedEmoteIds.Remove(emote.Id);
                if (selectedEmote?.Id == emote.Id)
                    SyncSelectedEmoteFromIds();
            }

            if (selectedEmoteIds.Count > 1 && selectedEmoteIds.Contains(emote.Id))
            {
                if (ImGui.MenuItem($"Delete {selectedEmoteIds.Count} Selected"))
                    DeleteSelectedEmotes();
            }
        });

        // Tooltip with emote info
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            var defaultCmd = emote.GetDefaultCommand();
            var cmdText = defaultCmd == null || string.IsNullOrWhiteSpace(defaultCmd.Command) ? "(none)" : defaultCmd.Command;
            if (emote.Commands.Count > 1)
                cmdText += $" (+{emote.Commands.Count - 1} more)";
            ImGui.Text($"Command: {cmdText}");
            if (hasModAssociation)
                ImGui.Text($"Mods: {emote.AssociatedMods.Count} associated");
            ImGui.Text("Double-click to use | Drag to reorder");
            ImGui.EndTooltip();
        }
    }

    private void DrawFilteredEmotes()
    {
        // Cache the filtered list — recomputing it every frame allocates for an identical result
        if (cachedSearchQuery != searchQuery || cachedSearchRevision != plugin.Configuration.Revision)
        {
            cachedSearchResults = emoteManager.SearchEmotes(searchQuery);
            cachedSearchQuery = searchQuery;
            cachedSearchRevision = plugin.Configuration.Revision;
        }

        var emotes = cachedSearchResults;
        for (var i = 0; i < emotes.Count; i++)
            DrawEmoteItem(emotes[i], null);

        if (emotes.Count == 0)
        {
            ImGui.TextDisabled("No emotes found.");
            ImGui.TextDisabled("Try a different search term.");
        }
    }

    private void DrawSidebarToolbar()
    {
        var buttonCount = 5;
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var buttonSize = new Vector2(totalWidth / buttonCount, ImGui.GetFrameHeight());

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        {
            // 1. Add emote (opens popup)
            if (ImGuiComponents.IconButton("##AddEmote", FontAwesomeIcon.Plus, buttonSize))
            {
                newEmotePopupName = string.Empty;
                ImGui.OpenPopup("##AddEmotePopup");
            }
            if (ImGui.IsItemHovered())
                DrawPaddedTooltip("Add a new emote.");

            // 2. Add folder (opens popup)
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##AddFolder", FontAwesomeIcon.FolderPlus, buttonSize))
            {
                newFolderPopupName = string.Empty;
                ImGui.OpenPopup("##AddFolderPopup");
            }
            if (ImGui.IsItemHovered())
                DrawPaddedTooltip("Create a new, empty folder.");

            // 3. Duplicate selected
            ImGui.SameLine();
            var canDuplicate = selectedEmote != null && selectedEmoteIds.Count == 1;
            using (ImRaii.Disabled(!canDuplicate))
            {
                if (ImGuiComponents.IconButton("##DuplicateSelected", FontAwesomeIcon.Clone, buttonSize) && canDuplicate)
                {
                    var dup = emoteManager.DuplicateEmote(selectedEmote!);
                    SelectSingleEmote(dup);
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                DrawPaddedTooltip(canDuplicate ? "Duplicate selected emote." : "No emote selected.");

            // 4. Rename selected
            ImGui.SameLine();
            var canRename = selectedFolder != null || (selectedEmote != null && selectedEmoteIds.Count == 1);
            using (ImRaii.Disabled(!canRename))
            {
                if (ImGuiComponents.IconButton("##RenameSelected", FontAwesomeIcon.Edit, buttonSize) && canRename)
                {
                    if (selectedFolder != null)
                        StartFolderRename(selectedFolder);
                    else
                    {
                        isRenaming = true;
                        renameBuffer = selectedEmote!.Name;
                    }
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                DrawPaddedTooltip(canRename
                    ? selectedFolder != null ? "Rename selected folder." : "Rename selected emote."
                    : "No item selected.");

            // 5. Delete selected (requires Ctrl+Shift)
            ImGui.SameLine();
            var io = ImGui.GetIO();
            var keysHeld = io.KeyCtrl && io.KeyShift;
            var anySelected = selectedFolder != null || selectedEmoteIds.Count > 0;
            var canDelete = anySelected && keysHeld;
            var itemName = selectedFolder != null ? "folder" : selectedEmoteIds.Count > 1 ? "emotes" : "emote";
            using (ImRaii.Disabled(!canDelete))
            {
                if (ImGuiComponents.IconButton("##DeleteSelected", FontAwesomeIcon.TrashAlt, buttonSize) && canDelete)
                {
                    if (selectedFolder != null)
                    {
                        emoteManager.RemoveFolder(selectedFolder);
                        ClearSelection();
                    }
                    else
                        DeleteSelectedEmotes();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                var tooltip = !anySelected
                    ? $"No {itemName} selected."
                    : $"Delete the currently selected {itemName}.\nThis can not be undone.";
                if (anySelected && !keysHeld)
                    tooltip += "\n\nHold Control and Shift while clicking to delete.";
                DrawPaddedTooltip(tooltip);
            }
        }

        // Popups (drawn outside zero-spacing style)
        DrawNameInputPopup("##AddEmotePopup", ref newEmotePopupName, name =>
        {
            var newEmote = emoteManager.AddEmote(name);
            SelectSingleEmote(newEmote);
        });
        DrawNameInputPopup("##AddFolderPopup", ref newFolderPopupName, name =>
        {
            var folder = emoteManager.AddFolder(name);
            SelectFolder(folder);
        });
    }

    private static void DrawPaddedTooltip(string text)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        ImGui.SetTooltip(text);
    }

    private static void DrawNameInputPopup(string popupId, ref string nameBuffer, Action<string> onConfirm)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        if (!ImGui.BeginPopup(popupId))
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputTextWithHint("##Name", "Enter New Name...", ref nameBuffer, 128, ImGuiInputTextFlags.EnterReturnsTrue)
            && nameBuffer.Length > 0)
        {
            onConfirm(nameBuffer);
            nameBuffer = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawSettingsPanel()
    {
        if (selectedFolder != null)
        {
            ImGui.Text($"Folder: {selectedFolder.Name}");
            ImGui.TextDisabled($"Contains {selectedFolder.Folders.Count} folders and {selectedFolder.Emotes.Count} emotes.");
            ImGui.Separator();

            if (ImGui.Button("Rename Folder"))
                StartFolderRename(selectedFolder);

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, RedButtonColor);
            if (ImGui.Button("Delete Folder"))
            {
                emoteManager.RemoveFolder(selectedFolder);
                ClearSelection();
            }
            ImGui.PopStyleColor();
            return;
        }

        if (selectedEmote == null)
        {
            ImGui.TextDisabled("Select an emote or folder from the list.");
            return;
        }

        if (selectedEmoteIds.Count > 1)
        {
            ImGui.Text($"{selectedEmoteIds.Count} emotes selected");
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Apply All Mods"))
            {
                var allEmotes = emoteManager.GetAllEmotes();
                foreach (var e in allEmotes.Where(e => selectedEmoteIds.Contains(e.Id)))
                    emoteManager.ApplyAllModSettings(e);
            }

            ImGui.SameLine();
            if (ImGui.Button("Disable All Mods"))
            {
                var allEmotes = emoteManager.GetAllEmotes();
                foreach (var e in allEmotes.Where(e => selectedEmoteIds.Contains(e.Id)))
                    emoteManager.DisableEmoteMods(e);
            }

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Button, RedButtonColor);
            if (ImGui.Button($"Delete {selectedEmoteIds.Count} Emotes"))
                DeleteSelectedEmotes();
            ImGui.PopStyleColor();
            return;
        }

        var emote = selectedEmote;

        // Rename mode
        if (isRenaming)
        {
            ImGui.Text("Rename:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputText("##RenameInput", ref renameBuffer, 128, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (!string.IsNullOrWhiteSpace(renameBuffer))
                {
                    emoteManager.RenameEmote(emote, renameBuffer);
                    isRenaming = false;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("OK") && !string.IsNullOrWhiteSpace(renameBuffer))
            {
                emoteManager.RenameEmote(emote, renameBuffer);
                isRenaming = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                isRenaming = false;
        }
        else
        {
            ImGui.Text($"Emote: {emote.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Rename"))
            {
                isRenaming = true;
                renameBuffer = emote.Name;
            }
        }

        ImGui.Separator();

        // Penumbra mod association
        ImGui.Text("Penumbra Mod Association");

        if (!penumbraService.Available)
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Penumbra is not available.");
        }
        else
        {
            // Collection selector
            var currentCollectionId = emote.PenumbraCollectionId;
            ImGui.Text("Collection:");
            ImGui.SameLine();
            var currentCollectionDisplay = currentCollectionId == Guid.Empty
                ? "(Current/Default)"
                : (cachedCollections.TryGetValue(currentCollectionId, out var colName) ? colName : currentCollectionId.ToString());

            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##CollectionSelect", currentCollectionDisplay))
            {
                if (ImGui.Selectable("(Current/Default)", currentCollectionId == Guid.Empty))
                {
                    emote.PenumbraCollectionId = Guid.Empty;
                    emoteManager.UpdateEmote(emote);
                }

                foreach (var (id, name) in cachedCollections)
                {
                    if (ImGui.Selectable($"{name}##{id}", id == currentCollectionId))
                    {
                        emote.PenumbraCollectionId = id;
                        emoteManager.UpdateEmote(emote);
                    }
                }

                ImGui.EndCombo();
            }

            // Auto-toggle setting
            ImGui.SameLine();
            var autoToggle = emote.AutoToggleMod;
            if (ImGui.Checkbox("Auto-apply on use", ref autoToggle))
            {
                emote.AutoToggleMod = autoToggle;
                emoteManager.UpdateEmote(emote);
            }

            ImGui.Spacing();

            // "Try Applying All" button
            if (emote.AssociatedMods.Count > 0)
            {
                var availWidth = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Try Applying All Associated Mods", new Vector2(availWidth, 0)))
                    emoteManager.ApplyAllModSettings(emote);
            }

            ImGui.Spacing();

            // Mod table
            DrawModTable(emote);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Emote icon + command list
        var iconSize = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        emoteIconHelper.DrawIcon(emote.GetDefaultCommand()?.Command ?? string.Empty, iconSize);
        ImGui.BeginGroup();
        ImGui.Text("Emote Commands:");
        ImGui.SameLine();
        if (ImGui.SmallButton("Detect from mods"))
            DetectCommandsFromMods(emote);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan the associated Penumbra mods for the emotes they change\nand add their commands below.");
        if (detectStatus is { } status && status.EmoteId == emote.Id)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(status.Message);
        }
        DrawCommandTable(emote);
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawModSettingsSection(emote);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Quick actions
        ImGui.Text("Quick Actions:");

        if (ImGui.Button("Use Emote"))
            emoteManager.UseEmote(emote);

        ImGui.SameLine();
        var previewing = emoteManager.IsPreviewing;
        if (previewing)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.55f, 0.1f, 1f));
        ImGui.BeginDisabled(!previewing);
        if (ImGui.Button("Stop Preview"))
            emoteManager.StopPreview();
        ImGui.EndDisabled();
        if (previewing)
            ImGui.PopStyleColor();
        if (previewing && ImGui.IsItemHovered())
            ImGui.SetTooltip("Cancel the running local preview and unlock your character.");

        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            var dup = emoteManager.DuplicateEmote(emote);
            SelectSingleEmote(dup);
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, RedButtonColor);
        if (ImGui.Button("Delete Emote"))
        {
            emoteManager.RemoveEmote(emote);
            selectedEmoteIds.Remove(emote.Id);
            SyncSelectedEmoteFromIds();
        }
        ImGui.PopStyleColor();
    }

    // cpose variant animation files: emote/pose0N = standing idle, s_ = chair sit,
    // j_ = ground sit, l_ = doze, b_ = weapon drawn (no playable command — skipped)
    private static readonly Regex PoseFileRegex = new(
        @"emote/(s_|j_|l_|b_)?pose(\d{2})_(?:loop|start)\.pap",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any emote animation file (.pap under an emote path). Distinguishes an actual
    // animation replacer from a VFX/sound-only mod that Penumbra still reports as
    // changing "Emote: X" because the effect is tied to that emote.
    private static readonly Regex EmoteAnimationRegex = new(
        @"emote/[^""]*\.pap",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void DetectCommandsFromMods(EmoteEntry emote)
    {
        if (emote.AssociatedMods.Count == 0)
        {
            detectStatus = (emote.Id, "No associated mods to scan.");
            return;
        }

        if (!penumbraService.Available)
        {
            detectStatus = (emote.Id, "Penumbra is not available.");
            return;
        }

        // (command, 1-based pose or 0 for generic) -> display name, deduplicated across mods
        var detected = new Dictionary<(string Command, int Pose), string>();
        foreach (var mod in emote.AssociatedMods)
        {
            foreach (var itemName in penumbraService.GetChangedItems(mod.ModDirectory, mod.ModName).Keys)
            {
                var resolved = emoteIconHelper.ResolveEmote(itemName);
                if (resolved != null)
                    detected.TryAdd((resolved.Value.Command, 0), resolved.Value.Name);
                else
                    Plugin.Log.Debug($"Changed item '{itemName}' in '{mod.ModDirectory}' did not resolve to an emote command.");
            }
        }

        DetectPoseFiles(emote, detected);

        var added = 0;
        foreach (var (command, name, pose) in FinalizeDetectedRows(detected))
        {
            if (emote.Commands.Any(c => c.PoseIndex == pose && c.Command.TrimStart('/').Equals(
                    command.TrimStart('/'), StringComparison.OrdinalIgnoreCase)))
                continue;

            var row = new EmoteCommandEntry { Name = name, Command = command, PoseIndex = pose };
            emote.Commands.Add(row);
            added++;
        }

        if (added > 0)
        {
            if (emote.GetDefaultCommand() == null || emote.DefaultCommandId == Guid.Empty)
                emote.DefaultCommandId = emote.Commands.First(c => c.Enabled).Id;
            emoteManager.UpdateEmote(emote);
        }

        detectStatus = (emote.Id, added > 0
            ? $"Added {added} command(s)."
            : detected.Count > 0
                ? "All detected commands already exist."
                : "No emote changes found in associated mods.");
    }

    /// <summary>
    /// Scan the associated mods' option files on disk for cpose variant animations
    /// (e.g. emote/l_pose02_loop.pap = doze pose 3) and add pose-specific entries.
    /// </summary>
    private void DetectPoseFiles(EmoteEntry emote, Dictionary<(string Command, int Pose), string> detected)
    {
        var modRoot = penumbraService.GetModRootDirectory();
        if (string.IsNullOrEmpty(modRoot))
            return;

        foreach (var mod in emote.AssociatedMods)
            CollectPoseRowsFromDisk(modRoot, mod.ModDirectory, detected);
    }

    /// <summary>
    /// Scan one mod folder's option files for cpose variant animations, adding pose rows.
    /// Returns whether the mod replaces any emote animation (.pap) file — false for a
    /// VFX/sound-only mod, used to keep those out of the scanner.
    /// </summary>
    private bool CollectPoseRowsFromDisk(string modRoot, string modDirectory, Dictionary<(string Command, int Pose), string> detected)
    {
        var modDir = Path.Combine(modRoot, modDirectory);
        if (!Directory.Exists(modDir))
            return false;

        // Only the option files list replaced game paths — skip meta.json and friends
        IEnumerable<string> optionFiles = Directory.EnumerateFiles(modDir, "group_*.json", SearchOption.TopDirectoryOnly);
        var defaultMod = Path.Combine(modDir, "default_mod.json");
        if (File.Exists(defaultMod))
            optionFiles = optionFiles.Append(defaultMod);

        var hasEmoteAnimation = false;
        foreach (var jsonFile in optionFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(jsonFile);
            }
            catch
            {
                continue;
            }

            if (!hasEmoteAnimation && EmoteAnimationRegex.IsMatch(text))
                hasEmoteAnimation = true;

            foreach (Match match in PoseFileRegex.Matches(text))
            {
                byte? poseType = match.Groups[1].Value.ToLowerInvariant() switch
                {
                    "" => 0,   // standing idle (/changepose)
                    "s_" => 2, // chair sit
                    "j_" => 3, // ground sit
                    "l_" => 4, // doze
                    _ => null, // b_ = weapon drawn — no playable command
                };
                if (poseType == null)
                    continue;

                var info = emoteIconHelper.GetPoseTypeInfo(poseType.Value);
                if (info == null)
                    continue;

                // File pose0N replaces cpose index N; our PoseIndex is 1-based
                if (!int.TryParse(match.Groups[2].Value, out var fileIndex))
                    continue;

                var pose = fileIndex + 1;
                var baseName = poseType.Value == 0 ? "Idle" : info.Value.Name;
                detected.TryAdd((info.Value.Command, pose), $"{baseName} Pose {pose}");
            }
        }

        return hasEmoteAnimation;
    }

    /// <summary>
    /// Turn a detected (command, pose) map into ordered command rows: pose-specific rows
    /// supersede the generic row for the same command; normal emotes before expressions,
    /// pose variants ascending.
    /// </summary>
    private List<(string Command, string Name, int Pose)> FinalizeDetectedRows(
        Dictionary<(string Command, int Pose), string> detected)
    {
        var poseCommands = new HashSet<string>(
            detected.Keys.Where(k => k.Pose > 0).Select(k => k.Command), StringComparer.OrdinalIgnoreCase);
        foreach (var key in detected.Keys.Where(k => k.Pose == 0 && poseCommands.Contains(k.Command)).ToList())
            detected.Remove(key);

        return detected
            .OrderBy(kv => emoteIconHelper.IsExpressionCommand(kv.Key.Command) ? 1 : 0)
            .ThenBy(kv => kv.Key.Pose)
            .Select(kv => (kv.Key.Command, kv.Value, kv.Key.Pose))
            .ToList();
    }

    private void DrawCommandTable(EmoteEntry emote)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        if (ImGui.BeginTable("##CommandTable", 7, flags))
        {
            var checkWidth = ImGui.GetFrameHeight() + 4;
            ImGui.TableSetupColumn("##On", ImGuiTableColumnFlags.WidthFixed, checkWidth);
            ImGui.TableSetupColumn("##Default", ImGuiTableColumnFlags.WidthFixed, checkWidth);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.30f);
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthStretch, 0.40f);
            ImGui.TableSetupColumn("Alias", ImGuiTableColumnFlags.WidthStretch, 0.30f);
            ImGui.TableSetupColumn("Pose", ImGuiTableColumnFlags.WidthFixed, 44);
            ImGui.TableSetupColumn("##CmdActions", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableHeadersRow();

            EmoteCommandEntry? toRemove = null;
            (int From, int To)? pendingMove = null;
            // Render the radio from the EFFECTIVE default (enabled-only fallback) so the
            // marker always matches what actually plays.
            var effectiveDefault = emote.GetDefaultCommand();
            for (var i = 0; i < emote.Commands.Count; i++)
            {
                var cmd = emote.Commands[i];
                ImGui.TableNextRow();
                ImGui.PushID(i);

                // Enabled checkbox
                ImGui.TableNextColumn();
                var enabled = cmd.Enabled;
                if (ImGui.Checkbox("##cmdEnabled", ref enabled))
                {
                    cmd.Enabled = enabled;
                    emoteManager.UpdateEmote(emote);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enabled");

                // Default radio
                ImGui.TableNextColumn();
                if (ImGui.RadioButton("##cmdDefault", effectiveDefault?.Id == cmd.Id))
                {
                    emote.DefaultCommandId = cmd.Id;
                    emoteManager.UpdateEmote(emote);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Default command (used by double-click, quick access and play)");

                // Name
                ImGui.TableNextColumn();
                var cmdName = cmd.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##cmdName", "Name...", ref cmdName, 128))
                {
                    cmd.Name = cmdName;
                    emoteManager.UpdateEmote(emote);
                }

                // Command
                ImGui.TableNextColumn();
                var command = cmd.Command;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##cmdCommand", "/dance", ref command, 256))
                {
                    cmd.Command = command;
                    emoteManager.UpdateEmote(emote);
                }

                // Alias
                ImGui.TableNextColumn();
                var cmdAlias = cmd.Alias;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##cmdAlias", "e1", ref cmdAlias, 64))
                {
                    cmd.Alias = cmdAlias;
                    emoteManager.UpdateEmote(emote);
                }
                if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(cmd.Alias))
                    ImGui.SetTooltip($"/emotes {cmd.Alias}");

                // Pose selector — only for pose-mode emotes (sit/groundsit/doze)
                ImGui.TableNextColumn();
                if (emoteIconHelper.GetPoseType(cmd.Command) != null)
                {
                    var pose = cmd.PoseIndex;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##cmdPose", ref pose, 0, 0))
                    {
                        cmd.PoseIndex = Math.Clamp(pose, 0, 9);
                        emoteManager.UpdateEmote(emote);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Pose number (as cycled by /cpose) to start this emote in.\n0 = keep the current pose selection.");
                }
                else
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled("-");
                }

                // Actions
                ImGui.TableNextColumn();
                if (ImGuiComponents.IconButton("##cmdPlay", FontAwesomeIcon.Play))
                    emoteManager.UseEmote(emote, cmd);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play this command");

                ImGui.SameLine(0, 2);
                ImGui.BeginDisabled(GetPreviewTimelineCached(cmd) == null);
                if (ImGuiComponents.IconButton("##cmdPreview", FontAwesomeIcon.Eye))
                    emoteManager.PreviewEmote(emote, cmd);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Preview the animation locally — only you see it.\nMods are applied; your character is locked until you press Stop Preview.");

                ImGui.SameLine(0, 2);
                ImGui.BeginDisabled(i == 0);
                if (ImGuiComponents.IconButton("##cmdUp", FontAwesomeIcon.ArrowUp))
                    pendingMove = (i, i - 1);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move up");

                ImGui.SameLine(0, 2);
                ImGui.BeginDisabled(i == emote.Commands.Count - 1);
                if (ImGuiComponents.IconButton("##cmdDown", FontAwesomeIcon.ArrowDown))
                    pendingMove = (i, i + 1);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move down");

                ImGui.SameLine(0, 2);
                ImGui.PushStyleColor(ImGuiCol.Button, RedButtonColor);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, RedButtonActiveColor);
                if (ImGuiComponents.IconButton("##cmdDel", FontAwesomeIcon.Times))
                    toRemove = cmd;
                ImGui.PopStyleColor(2);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove command");

                ImGui.PopID();
            }

            if (pendingMove is { } move && move.To >= 0 && move.To < emote.Commands.Count)
            {
                (emote.Commands[move.From], emote.Commands[move.To]) = (emote.Commands[move.To], emote.Commands[move.From]);
                emoteManager.UpdateEmote(emote);
            }

            if (toRemove != null)
            {
                emote.Commands.Remove(toRemove);
                if (emote.DefaultCommandId == toRemove.Id)
                {
                    // Prefer an enabled row so the marker matches what will play
                    emote.DefaultCommandId = emote.Commands.FirstOrDefault(c => c.Enabled)?.Id
                                             ?? emote.Commands.FirstOrDefault()?.Id
                                             ?? Guid.Empty;
                }
                emoteManager.UpdateEmote(emote);
            }

            ImGui.EndTable();
        }

        if (ImGuiComponents.IconButton("##cmdAdd", FontAwesomeIcon.Plus))
        {
            var row = new EmoteCommandEntry();
            emote.Commands.Add(row);
            if (emote.Commands.Count == 1)
                emote.DefaultCommandId = row.Id;
            emoteManager.UpdateEmote(emote);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Add command");
    }

    private HashSet<string> GetModEmoteCommands(ModAssociation mod)
    {
        if (!cachedModEmoteCommands.TryGetValue(mod.ModDirectory, out var commands))
        {
            commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var itemName in penumbraService.GetChangedItems(mod.ModDirectory, mod.ModName).Keys)
            {
                var resolved = emoteIconHelper.ResolveEmote(itemName);
                if (resolved != null)
                    commands.Add(resolved.Value.Command);
            }

            cachedModEmoteCommands[mod.ModDirectory] = commands;
        }

        return commands;
    }

    /// <summary> Index over all installed mods: emote command -> mod directories that change it. </summary>
    private Dictionary<string, List<string>> GetEmoteSlotIndex()
    {
        if (cachedEmoteSlotIndex == null)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, itemNames) in penumbraService.GetAllChangedItemNames())
            {
                foreach (var itemName in itemNames)
                {
                    var resolved = emoteIconHelper.ResolveEmote(itemName);
                    if (resolved == null)
                        continue;

                    if (!index.TryGetValue(resolved.Value.Command, out var list))
                        index[resolved.Value.Command] = list = new List<string>();
                    list.Add(dir);
                }
            }

            cachedEmoteSlotIndex = index;
        }

        return cachedEmoteSlotIndex;
    }

    private (bool Enabled, int Priority) GetModEnabledState(string modDirectory, Guid collectionId)
    {
        var key = (collectionId, modDirectory);
        if (!cachedModEnabledState.TryGetValue(key, out var state))
        {
            var (enabled, priority, _) = penumbraService.GetModSettings(modDirectory, collectionId);
            state = (enabled, priority);
            cachedModEnabledState[key] = state;
        }

        return state;
    }

    private Dictionary<string, (string Text, bool Winning)> GetEmoteConflicts(EmoteEntry emote)
    {
        if (conflictCacheEmoteId != emote.Id ||
            conflictCacheTick != lastCacheRefresh ||
            conflictCacheRevision != plugin.Configuration.Revision)
        {
            cachedConflicts = ComputeEmoteConflicts(emote);
            conflictCacheEmoteId = emote.Id;
            conflictCacheTick = lastCacheRefresh;
            conflictCacheRevision = plugin.Configuration.Revision;
        }

        return cachedConflicts;
    }

    /// <summary>
    /// Per mod directory, a description of the emote commands it shares with other enabled
    /// associations of the same emote or with mods enabled in the target collection, plus
    /// whether this mod out-prioritizes every rival. Empty when there are no conflicts.
    /// </summary>
    private Dictionary<string, (string Text, bool Winning)> ComputeEmoteConflicts(EmoteEntry emote)
    {
        var result = new Dictionary<string, (string Text, bool Winning)>();
        if (!penumbraService.Available || emote.AssociatedMods.Count == 0)
            return result;

        void AddLine(string modDirectory, string line, bool lineWins)
            => result[modDirectory] = result.TryGetValue(modDirectory, out var existing)
                ? (existing.Text + "\n" + line, existing.Winning && lineWins)
                : (line, lineWins);

        // Conflicts between the emote's own associated mods
        var byCommand = new Dictionary<string, List<ModAssociation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in emote.AssociatedMods)
        {
            if (!mod.Enabled)
                continue;

            foreach (var cmd in GetModEmoteCommands(mod))
            {
                if (!byCommand.TryGetValue(cmd, out var list))
                    byCommand[cmd] = list = new List<ModAssociation>();
                list.Add(mod);
            }
        }

        foreach (var (cmd, mods) in byCommand)
        {
            if (mods.Count < 2)
                continue;

            foreach (var mod in mods)
            {
                var rivals = mods.Where(m => m != mod).ToList();
                var others = rivals.Select(m => $"{m.DisplayName} (priority {m.Priority})");
                var wins = rivals.All(m => mod.Priority > m.Priority);
                AddLine(mod.ModDirectory, $"{cmd} — also changed by associated mod {string.Join(", ", others)}", wins);
            }
        }

        // Conflicts with other mods enabled in the target collection (e.g. permanently enabled emote mods)
        var slotIndex = GetEmoteSlotIndex();
        var associatedDirs = new HashSet<string>(
            emote.AssociatedMods.Select(m => m.ModDirectory), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in emote.AssociatedMods)
        {
            if (!mod.Enabled)
                continue;

            foreach (var cmd in GetModEmoteCommands(mod))
            {
                if (!slotIndex.TryGetValue(cmd, out var dirs))
                    continue;

                foreach (var dir in dirs)
                {
                    if (associatedDirs.Contains(dir))
                        continue;

                    var (enabled, priority) = GetModEnabledState(dir, emote.PenumbraCollectionId);
                    if (!enabled)
                        continue;

                    var name = cachedMods.TryGetValue(dir, out var n) && !string.IsNullOrWhiteSpace(n) ? n : dir;
                    AddLine(mod.ModDirectory, $"{cmd} — also changed by enabled collection mod {name} (priority {priority})",
                        mod.Priority > priority);
                }
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, (string[] Options, GroupType Type)>? GetModOptions(ModAssociation mod)
    {
        if (!cachedModOptions.TryGetValue(mod.ModDirectory, out var options))
        {
            options = penumbraService.GetAvailableModSettings(mod.ModDirectory, mod.ModName);
            cachedModOptions[mod.ModDirectory] = options;
        }

        return options;
    }

    private void DrawModSettingsSection(EmoteEntry emote)
    {
        ImGui.Text("Mod Settings");

        if (!penumbraService.Available)
        {
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Penumbra is not available.");
            return;
        }

        if (emote.AssociatedMods.Count == 0)
        {
            ImGui.TextDisabled("No associated mods.");
            return;
        }

        for (var i = 0; i < emote.AssociatedMods.Count; i++)
        {
            var mod = emote.AssociatedMods[i];
            ImGui.PushID(i);

            var displayName = mod.DisplayName;
            if (ImGui.CollapsingHeader($"{displayName}##settings"))
            {
                // Older associations may have no stored settings — snapshot from Penumbra
                // so the editor shows exactly what applying will do.
                if (mod.Settings.Count == 0 && settingsSyncAttempted.Add((emote.Id, mod.ModDirectory)))
                    emoteManager.SyncModSettingsFromPenumbra(emote, mod);

                ImGui.Indent();
                DrawModSettingsEditor(emote, mod);
                ImGui.Unindent();
            }

            ImGui.PopID();
        }
    }

    private void DrawModSettingsEditor(EmoteEntry emote, ModAssociation mod)
    {
        var groups = GetModOptions(mod);
        if (groups == null)
        {
            ImGui.TextDisabled("Mod not found in Penumbra.");
            return;
        }

        if (groups.Count == 0)
        {
            ImGui.TextDisabled("This mod has no settings.");
            return;
        }

        var changed = false;
        foreach (var (groupName, (options, type)) in groups)
        {
            if (options.Length == 0)
                continue;

            ImGui.PushID(groupName);

            var hasOverride = mod.Settings.TryGetValue(groupName, out var selected);

            ImGui.Text(groupName);
            if (!hasOverride)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(mod default)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No override stored — when this emote applies the mod,\nPenumbra sets this group to the mod's default settings.");
            }

            ImGui.Indent();
            if (type == GroupType.Single)
            {
                var current = hasOverride && selected!.Count > 0 ? selected[0] : null;
                ImGui.SetNextItemWidth(250);
                if (ImGui.BeginCombo("##singleGroup", current ?? "(mod default)"))
                {
                    if (ImGui.Selectable("(mod default)", current == null) && hasOverride)
                    {
                        mod.Settings.Remove(groupName);
                        changed = true;
                    }

                    foreach (var option in options)
                    {
                        if (ImGui.Selectable(option, option == current))
                        {
                            mod.Settings[groupName] = new List<string> { option };
                            changed = true;
                        }
                    }

                    ImGui.EndCombo();
                }
            }
            else // Multi, Imc, Combining — independent toggles
            {
                foreach (var option in options)
                {
                    var isOn = hasOverride && selected!.Contains(option);
                    if (ImGui.Checkbox(option, ref isOn))
                    {
                        if (!hasOverride)
                        {
                            // Seed the new override from the mod's current values in Penumbra —
                            // otherwise applying [clicked option] alone would silently turn off
                            // every other option the group has enabled by default.
                            var (_, _, current) = penumbraService.GetModSettings(
                                mod.ModDirectory, emote.PenumbraCollectionId, mod.ModName);
                            selected = current.TryGetValue(groupName, out var cur) && cur != null
                                ? new List<string>(cur)
                                : new List<string>();
                            mod.Settings[groupName] = selected;
                            hasOverride = true;
                        }

                        if (isOn)
                        {
                            if (!selected!.Contains(option))
                                selected.Add(option);
                        }
                        else
                        {
                            selected!.Remove(option);
                        }

                        changed = true;
                    }
                }
            }

            ImGui.Unindent();
            ImGui.PopID();
        }

        if (changed)
        {
            emoteManager.UpdateEmote(emote);
            emoteManager.ApplyModSetting(emote, mod); // apply live + redraw
        }
    }

    private void DrawModTable(EmoteEntry emote)
    {
        // Minimal table: no outer borders and no horizontal separator lines between header/rows.
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
                  | ImGuiTableFlags.PadOuterX;
        if (!ImGui.BeginTable("##ModAssocTable", 4, flags))
            return;

        // Columns: Mod Name | State | Priority | Actions
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableHeadersRow();

        var conflicts = GetEmoteConflicts(emote);

        ModAssociation? toRemove = null;
        for (var i = 0; i < emote.AssociatedMods.Count; i++)
        {
            var mod = emote.AssociatedMods[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            // Mod Name (clickable to open in Penumbra)
            ImGui.TableNextColumn();
            var displayName = mod.DisplayName;
            var rowHeight = ImGui.GetFrameHeight();
            var textHeight = ImGui.GetTextLineHeight();
            var pad = (rowHeight - textHeight) * 0.5f;
            var cursorPos = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorPos + pad);

            if (conflicts.TryGetValue(mod.ModDirectory, out var conflict))
            {
                ImGui.PushFont(UiBuilder.IconFont);
                if (conflict.Winning)
                    ImGui.TextColored(new Vector4(0.35f, 0.9f, 0.35f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
                else
                    ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
                ImGui.PopFont();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(conflict.Winning
                        ? $"Shares emote slots with other mods, but this mod has the highest priority and wins:\n{conflict.Text}"
                        : $"Shares emote slots with other mods:\n{conflict.Text}\nThe mod with the higher (or equal) priority wins — raise this mod's priority to take over.");
                }
                ImGui.SameLine();
            }

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.78f, 1.0f, 1));
            if (ImGui.Selectable($"{displayName}##modname", false))
            {
                penumbraService.OpenModInPenumbra(mod.ModDirectory, mod.ModName);
            }
            ImGui.PopStyleColor();

            // State toggle
            ImGui.TableNextColumn();
            var modEnabled = mod.Enabled;
            if (ImGui.Checkbox("##state", ref modEnabled))
            {
                mod.Enabled = modEnabled;
                emoteManager.UpdateEmote(emote);
            }

            // Priority
            ImGui.TableNextColumn();
            var priority = mod.Priority;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##prio", ref priority, 0, 0))
            {
                mod.Priority = priority;
                emoteManager.UpdateEmote(emote);
            }

            // Inline action buttons (compact row)
            ImGui.TableNextColumn();
            if (ImGuiComponents.IconButton($"apply_{i}", FontAwesomeIcon.Play))
                emoteManager.ApplyModSetting(emote, mod);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply");

            ImGui.SameLine(0, 2);
            if (ImGuiComponents.IconButton($"reapply_{i}", FontAwesomeIcon.Sync))
                emoteManager.ReapplyModFromPenumbra(emote, mod);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reapply from Penumbra");

            ImGui.SameLine(0, 2);
            ImGui.PushStyleColor(ImGuiCol.Button, RedButtonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, RedButtonActiveColor);
            if (ImGuiComponents.IconButton($"del_{i}", FontAwesomeIcon.Times))
                toRemove = mod;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove");

            ImGui.PopID();
        }

        // Remove after iteration
        if (toRemove != null)
        {
            emote.AssociatedMods.Remove(toRemove);
            emoteManager.UpdateEmote(emote);
        }

        ImGui.EndTable();

        // Add mod row — outside the table for a cleaner look
        ImGui.Spacing();
        var comboWidth = ImGui.GetContentRegionAvail().X - 30;
        if (ImGuiComponents.IconButton("add", FontAwesomeIcon.Plus))
        {
            // Handled by combo
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(comboWidth);
        ImGui.SetNextWindowSizeConstraints(new Vector2(comboWidth, 0), new Vector2(comboWidth, 400));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginCombo("##AddModCombo", "Add mod..."))
        {
            // Filter pinned above the scrolling list — flush and square (Glamourer-style)
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##AddModSearch", "Search mods...", ref addModSearchQuery, 128);
            ImGui.PopStyleVar();

            if (ImGui.BeginChild("##AddModItems", new Vector2(-1, 320)))
            {
                foreach (var (dir, name) in cachedMods)
                {
                    if (emote.AssociatedMods.Any(existing => existing.ModDirectory == dir))
                        continue;

                    if (!string.IsNullOrWhiteSpace(addModSearchQuery) &&
                        !dir.Contains(addModSearchQuery, StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(addModSearchQuery, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var label = string.IsNullOrWhiteSpace(name) ? dir : name;
                    if (ImGui.Selectable($"{label}##{dir}"))
                    {
                        var newMod = new ModAssociation
                        {
                            ModDirectory = dir,
                            ModName = name,
                            Enabled = true,
                            Priority = 1,
                        };
                        emote.AssociatedMods.Add(newMod);
                        // Snapshot the mod's current settings so the association applies what Penumbra shows now
                        emoteManager.SyncModSettingsFromPenumbra(emote, newMod);
                        emoteManager.UpdateEmote(emote);
                        addModSearchQuery = string.Empty;
                        // Selectables inside a child window don't auto-close the popup
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.EndChild();
            ImGui.EndCombo();
        }

        ImGui.PopStyleVar();
    }

    private void RefreshCacheIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - lastCacheRefresh < 5) return;

        lastCacheRefresh = now;
        cachedModOptions.Clear();
        cachedModEnabledState.Clear();

        // The slot index walks every installed mod's changed items — refresh it far
        // less often than the cheap caches to avoid periodic frame hitches.
        if (now - lastSlotIndexRefresh >= 60)
        {
            lastSlotIndexRefresh = now;
            cachedEmoteSlotIndex = null;
            cachedModEmoteCommands.Clear();
        }

        if (penumbraService.Available)
        {
            // Case-insensitive copy: mod directories from exports may differ in case
            var mods = penumbraService.GetMods();
            var map = new Dictionary<string, string>(mods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, name) in mods)
                map.TryAdd(dir, name);
            cachedMods = map;
            cachedCollections = penumbraService.GetCollectionList();
        }
    }
}
