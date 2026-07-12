# Emote Plugin

A Dalamud plugin for managing and using custom emotes with Penumbra mod integration in FINAL FANTASY XIV. Organize your emotes into folders, associate Penumbra mods that auto-toggle when an emote plays, and trigger them from a searchable list or a floating quick-access widget.

## Features

### Emotes & Organization
- **Folder Tree** — Organize emotes into nested folders with drag-and-drop reordering
- **Emote Management** — Add, remove, rename, and duplicate emotes
- **Multiple Commands per Emote** — Each emote can hold several commands, each with its own name, alias, enabled toggle, and a default marker; reorder them freely
- **Emote Aliases** — Assign short aliases for fast command-line usage (`/emotes <alias>`)
- **Search & Filter** — Filter your list by emote name, command, or alias
- **Game Icons** — Displays the official FFXIV emote icon next to each emote
- **Double-Click to Use** — Double-click an emote in the sidebar to play it instantly

### Penumbra Integration
- **Mod Association** — Associate Penumbra mods with emotes and auto-toggle them using temporary settings
- **Inline Mod Settings** — View and edit each associated mod's option groups directly in the emote editor, applied live as a preview
- **Detect from Mods** — Scan an emote's associated mods for the emotes they change and add the matching command rows automatically (facial expressions listed last)
- **Mod Library Scanner** — Scan every installed Penumbra mod for emote animations and bulk-import the matches as ready-made emotes, with a review table to rename, untick, or pick a destination folder (mods already in use are skipped by default)
- **Animation Preview** — Preview a command's animation locally with mods applied — only you see it; your character is locked until you press Stop Preview
- **Conflict Warnings** — Mods sharing the same emote slot — including mods enabled permanently in the collection — are flagged: yellow when another mod wins, green when this mod's priority wins
- **Auto Redraw** — Automatically redraws your character after mod changes so animations update immediately

### Poses
- **Direct Pose Selection** — Sit, ground sit, doze, and standing idle (`/changepose`) commands can specify a `/cpose` pose number and land directly in that pose; replaying the row while already in the stance switches the pose in place
- **Pose Detection** — Detect from Mods reads which pose slots a mod replaces and adds pose-ready rows (e.g. "Doze Pose 3")
- **Sit/Doze Anywhere** — Optional setting to play `/sit` and `/doze` without a chair or bed (uses the game's hidden furniture emote)

### Sharing & Convenience
- **Import / Export** — Save and share emote sets as JSON. Import is selective (tick/untick individual emotes) with a review table for remapping or dropping mods that are missing on your machine; same-named folders merge instead of duplicating
- **Quick Access Widget** — Floating borderless widget with a filterable dropdown to quickly select and play emotes
- **What's New** — A changelog window shown once after each update

## Usage

| Command | Description |
|---------|-------------|
| `/emotes` | Toggle the main window |
| `/emotes <alias>` | Play the emote command bound to `<alias>` |

## Quick Access Widget

A small floating widget that stays on screen for fast emote switching. Includes a filterable combo box (with mouse-wheel selection), a play button, a button to disable all temporary mods, and a button to open the main window. When an emote has multiple enabled commands, each appears as its own entry (configurable in Settings). Toggle the widget in Settings.

## Building

### Prerequisites

- XIVLauncher and Dalamud installed (game run with Dalamud at least once)
- .NET 10 SDK installed
- Dalamud dev directory at default location (or set the `DALAMUD_HOME` environment variable)
- The `lib/Penumbra.Api` git submodule initialized (`git submodule update --init`)

### Build

```
dotnet build EmotePlugin\EmotePlugin.csproj
dotnet build EmotePlugin\EmotePlugin.csproj -c Release
```

Output: `EmotePlugin/bin/Debug/EmotePlugin.dll` (or `Release`)

### Activating In-Game

1. `/xlsettings` → Experimental → add full path to `EmotePlugin.dll` in Dev Plugin Locations
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable Emote Plugin
3. Use `/emotes` to open the plugin window
