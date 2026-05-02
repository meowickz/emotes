# Emote Plugin

A Dalamud plugin for managing and using custom emotes with Penumbra mod integration in FINAL FANTASY XIV.

## Features

- **Emote Management** — Add, remove, rename, and duplicate emotes
- **Penumbra Integration** — Associate Penumbra mods with emotes and auto-toggle them using temporary settings
- **Quick Access Widget** — Floating borderless widget with a filterable dropdown to quickly select and play emotes
- **Search & Filter** — Filter your emote list by name, command, or alias
- **Emote Aliases** — Assign short aliases to emotes for fast command-line usage (`/emotes <alias>`)
- **Game Icons** — Displays the official FFXIV emote icon next to each emote
- **Auto Redraw** — Automatically redraws your character after mod changes so animations update immediately
- **Double-Click to Use** — Double-click an emote in the sidebar to play it instantly

## Usage

| Command | Description |
|---------|-------------|
| `/emotes` | Toggle the main window |
| `/emotes <alias>` | Play an emote by its alias |

## Quick Access Widget

A small floating widget that stays on screen for fast emote switching. Includes a filterable combo box, play button, and a button to disable all temporary mods. Toggle it in Settings.

## Building

### Prerequisites

- XIVLauncher, FINAL FANTASY XIV, and Dalamud installed (game run with Dalamud at least once)
- .NET 10 SDK installed
- Dalamud dev directory at default location (or set `DALAMUD_HOME` environment variable)

### Build

```
dotnet build EmotePlugin\EmotePlugin.csproj
dotnet build EmotePlugin\EmotePlugin.csproj -c Release
```

Output: `EmotePlugin/bin/x64/Debug/EmotePlugin.dll` (or `Release`)

### Activating In-Game

1. `/xlsettings` → Experimental → add full path to `EmotePlugin.dll` in Dev Plugin Locations
2. `/xlplugins` → Dev Tools → Installed Dev Plugins → enable Emote Plugin
3. Use `/emotes` to open the plugin window

