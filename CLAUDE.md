# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

A Dalamud plugin that manages custom emotes and auto-toggles associated Penumbra mods when an emote is played. C#, .NET 10, built with the `Dalamud.NET.Sdk` MSBuild SDK.

## Build

```
dotnet build EmotePlugin\EmotePlugin.csproj
dotnet build EmotePlugin\EmotePlugin.csproj -c Release
```

Output: `EmotePlugin/bin/Debug/EmotePlugin.dll` (or `Release`). There are no tests.

Requirements: Dalamud dev directory must exist (game run with Dalamud at least once) or `DALAMUD_HOME` must be set — the SDK resolves Dalamud assemblies from there. The `lib/Penumbra.Api` git submodule must be initialized (`git submodule update --init`).

Testing changes requires loading the built DLL in-game as a dev plugin (`/xlsettings` → Dev Plugin Locations); there is no way to run it standalone.

## Architecture

All source lives in `EmotePlugin/` (~10 files). The layering:

- **`Plugin.cs`** — entry point (`IDalamudPlugin`). Holds Dalamud services as `[PluginService]` static properties (accessed elsewhere as `Plugin.Log`, `Plugin.PluginInterface`, etc.), wires up all components, and registers the `/emotes` command (no args = toggle window; with args = play emote by alias).
- **`PenumbraService.cs`** — the only place that talks to Penumbra, via IPC subscribers from the `Penumbra.Api` submodule. Every method guards on `Available` (Penumbra API breaking version >= 5) and swallows exceptions, returning safe defaults. Mod toggling uses Penumbra's *temporary* mod settings API, keyed by `PluginKey` so settings can be identified and removed; `Dispose` removes all temporary settings from every collection touched.
- **`EmoteManager.cs`** — business logic. Manages the emote/folder tree, and `UseEmote` orchestrates the core flow: disable previous emote's temp mods → apply this emote's mod settings → redraw character (only when needed: same emote command as last time, or `AlwaysRedraw`) → send the emote chat command via `FFXIVClientStructs` (`UIModule.ProcessChatBoxEntry`). The only `unsafe` code in the codebase is here in `EmoteManager`: the chat call, reading `EmoteController` pose state, executing hidden emotes via the hotbar scratch slot (Sit/Doze Anywhere), and the local animation preview, which writes character state Brio-style (save Mode/ModeParam/`Timeline.BaseOverride`, `SetMode(AnimLock)`, play timeline; a per-tick watchdog in `OnFrameworkUpdate` restores/releases on movement, mode takeover, or logout — never remove that watchdog or the `StopPreview()` at the top of `UseEmote`). Direct pose selection for sit/groundsit/doze/standing-idle works by queueing a pose change, waiting on `IFramework.Update` until the stance is entered, then sending `/cpose` the computed number of times (there is no game API to set a pose directly).
- **`Configuration.cs`** + **`EmoteEntry.cs`** / **`EmoteFolder.cs`** / **`ModAssociation.cs`** — serialized plugin config. Emotes live in a recursive folder tree (`RootFolder`); the legacy flat `Emotes` list is kept only for deserialization and migrated into the tree by `Configuration.Migrate()` on load. Entities carry `Guid Id`s used for tree operations. Every mutation goes through `EmoteManager`, which calls `configuration.Save()` immediately.
- **`Windows/`** — ImGui UI via Dalamud's `WindowSystem`. `MainWindow.cs` (the bulk of the UI: Emotes tab with sidebar tree, drag-drop, emote editor, plus a Settings tab), `QuickAccessWindow.cs` (floating borderless widget), and `WhatsNewWindow.cs` (changelog popup shown once per release; its `Changelog` array gates the auto-popup and must get a new entry per release). Windows call into `EmoteManager`/`PenumbraService`; they don't touch Penumbra IPC directly.

## Releases

Pushing a `v*` tag triggers `.github/workflows/release.yml`: builds via the shared xivdev/Penumbra actions, updates `repo.json` (the Dalamud custom plugin repository manifest at repo root, committed back to master by the bot), and creates a GitHub release with `EmotePlugin.zip`. To release: bump `<Version>` in `EmotePlugin/EmotePlugin.csproj` and the `Changelog` in `EmotePlugin/EmotePlugin.json`, commit, then tag. Don't hand-edit `repo.json` version fields — CI owns them.
