using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

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
    private long lastCacheRefresh;

    // Mod picker state
    private string modSearchQuery = string.Empty;
    private string addModSearchQuery = string.Empty;

    // Drag-and-drop state
    private EmoteEntry? draggedEmote;
    private EmoteFolder? draggedFolder;

    // Tree line state: header midpoint captured in DrawFolderNode
    private float lastFolderHeaderMidY;

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

    public override void Draw()
    {
        RefreshCacheIfNeeded();

        // Top bar: Add emote
        DrawTopBar();

        ImGui.Separator();

        // Two-panel layout
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

    private void DrawTopBar()
    {
        var penumbraStatus = penumbraService.Available && penumbraService.IsPenumbraEnabled();
        ImGui.TextColored(
            penumbraStatus ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0.3f, 0.3f, 1),
            penumbraStatus ? "Penumbra: Connected" : "Penumbra: Unavailable");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##TopBarSettings", FontAwesomeIcon.Cog))
        {
            plugin.ToggleConfigUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open settings");
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
            ImGui.Text($"Command: {(string.IsNullOrWhiteSpace(emote.EmoteCommand) ? "(none)" : emote.EmoteCommand)}");
            if (hasModAssociation)
                ImGui.Text($"Mods: {emote.AssociatedMods.Count} associated");
            ImGui.Text("Double-click to use | Drag to reorder");
            ImGui.EndTooltip();
        }
    }

    private void DrawFilteredEmotes()
    {
        var emotes = emoteManager.SearchEmotes(searchQuery);
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

        // Emote icon + command/alias
        var iconSize = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        emoteIconHelper.DrawIcon(emote.EmoteCommand, iconSize);
        ImGui.BeginGroup();
        var groupWidth = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cmdWidth = groupWidth * 0.6f;
        var aliasWidth = groupWidth - cmdWidth - spacing;

        ImGui.Text("Emote Command:");
        ImGui.SameLine(cmdWidth + spacing);
        ImGui.Text("Alias:");

        var emoteCommand = emote.EmoteCommand;
        ImGui.SetNextItemWidth(cmdWidth);
        if (ImGui.InputTextWithHint("##EmoteCommand", "/emote command (e.g. /dance)", ref emoteCommand, 256))
        {
            emote.EmoteCommand = emoteCommand;
            emoteManager.UpdateEmote(emote);
        }
        ImGui.SameLine();
        var alias = emote.Alias;
        ImGui.SetNextItemWidth(aliasWidth);
        if (ImGui.InputTextWithHint("##EmoteAlias", "e.g. e1 (/emotes e1)", ref alias, 64))
        {
            emote.Alias = alias;
            emoteManager.UpdateEmote(emote);
        }
        ImGui.EndGroup();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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

        // Quick actions
        ImGui.Text("Quick Actions:");

        if (ImGui.Button("Use Emote"))
            emoteManager.UseEmote(emote);

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

        ModAssociation? toRemove = null;
        for (var i = 0; i < emote.AssociatedMods.Count; i++)
        {
            var mod = emote.AssociatedMods[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            // Mod Name (clickable to open in Penumbra)
            ImGui.TableNextColumn();
            var displayName = string.IsNullOrWhiteSpace(mod.ModName) ? mod.ModDirectory : mod.ModName;
            var rowHeight = ImGui.GetFrameHeight();
            var textHeight = ImGui.GetTextLineHeight();
            var pad = (rowHeight - textHeight) * 0.5f;
            var cursorPos = ImGui.GetCursorPosY();
            ImGui.SetCursorPosY(cursorPos + pad);
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
        if (ImGui.BeginCombo("##AddModCombo", "Add mod..."))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##AddModSearch", "Search mods...", ref addModSearchQuery, 128);

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
                    emote.AssociatedMods.Add(new ModAssociation
                    {
                        ModDirectory = dir,
                        ModName = name,
                        Enabled = true,
                        Priority = 1,
                    });
                    emoteManager.UpdateEmote(emote);
                    addModSearchQuery = string.Empty;
                }
            }

            ImGui.EndCombo();
        }
    }

    private void RefreshCacheIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - lastCacheRefresh < 5) return;

        lastCacheRefresh = now;
        if (penumbraService.Available)
        {
            cachedMods = penumbraService.GetMods();
            cachedCollections = penumbraService.GetCollectionList();
        }
    }
}
