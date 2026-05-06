# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

"Remove Multiplayer Player Limit" (RMP) is a Slay the Spire 2 mod that increases the vanilla 4-player multiplayer lobby limit to 8-16 players. Built with Godot .NET SDK 4.5.1, targeting .NET 9.0, using Harmony for runtime IL patching.

## Build Commands

```bash
# Build the C# mod DLL (Debug)
dotnet build RemoveMultiplayerPlayerLimit.csproj -c Debug

# Build the .pck resource package (requires Godot 4.5.1 console)
godot --headless --path . --script "res://tools/build_pck.gd"

# Full release build (Windows PowerShell) — builds DLL + PCK + zip
powershell -File tools/build_release.ps1

# Full release build (Linux/macOS bash) — builds DLL + PCK + zip
bash tools/build_release.sh
```

The `libs/` directory must contain `sts2.dll` and `0Harmony.dll` (game assemblies, gitignored). These are required at compile time as `<Reference>` in the csproj but excluded from the repo.

## Architecture

### Entry Point & Partial Class Pattern

`ModEntry` is declared as a `partial class` in `src/ModEntry.cs` with the `[ModInitializer("Initialize")]` attribute. Every `src/Patches.*.cs` file declares `public static partial class ModEntry` and contains Harmony patch classes targeting specific game subsystems. All patches are applied in `Initialize()` via `new Harmony(...).PatchAll()`.

### Network Protocol Architecture

The mod uses a **dual-protocol concurrency** design:

1. **Official protocol channel** — extended via IL transpilers in `SerializationPatches.cs`. These modify serialization bit widths at the IL level:
   - Slot ID: 2 bits → 4 bits (supports 16 slots)
   - Lobby list length: 3 bits → 5 bits (supports 32 entries)
   - `TranspilerUtils.cs` provides generic IL rewriting utilities (find `ldc.i4` before method call, replace operand)

2. **Custom RMP protocol channel** — independent message/action types that coexist alongside official packets:
   - `RmpConfigSyncMessage` (INetMessage) — host broadcasts mod config to clients
   - `RmpSkipRelicNetAction` (INetAction) — relic skip voting without hacking official fields
   - `RmpProtocol.Bind()` wires the custom handler to an active `INetGameService` session

Both channels are automatically discovered and registered by the game's `ReflectionHelper.GetSubtypesInMods<T>()`.

### Configuration

`ProtocolConfig` is the single source of truth for all protocol constants and runtime-configurable values:
- `Vanilla*` — original game values (immutable, for transpiler matching)
- `*Bits` — extended bit widths (compile-time fixed)
- `TargetPlayerLimit` / `DifficultyScalingEnabled` — runtime configurable, saved to INI

Config is persisted to `config.ini` in the mod's directory. Legacy `config.json` is auto-migrated.

### Key Patch Domains

| File | What it patches | Technique |
|------|----------------|-----------|
| `LobbyPatches.cs` | `NetHostGameService`, `StartRunLobby` | Prefix on maxClients, Postfix on constructor |
| `SerializationPatches.cs` | 3 message types × (Serialize+Deserialize) | Transpiler (IL bit-width replacement) |
| `Patches.DifficultyScaling.cs` | `Creature`, `MultiplayerScalingModel` | Prefix + Transpiler (insert `GetEffectivePlayerCount` call) |
| `Patches.RestSite.cs` | `NRestSiteRoom` | Transpiler (container accessor swap), Prefix (event hooks) |
| `Patches.Merchant.cs` | `NMerchantRoom` | Postfix (reposition visuals) |
| `Patches.Treasure.cs` | `TreasureRoomRelicSynchronizer`, `NTreasureRoomRelicCollection` | Prefix/Postfix mix (holder extension, skip button, vote resolution) |
| `Patches.Settings.cs` | `NSettingsScreen` | Postfix (inject UI nodes), Postfix on paginator changes |
| `Patches.Tls.cs` | `TlsOptions.Client` | Prefix (intercept and return unsafe TLS on macOS multiplayer) |
| `Patches.Linux.cs` | None (runs at init) | `dlopen` with RTLD_GLOBAL to preload Harmony deps |

### Reflection-Heavy Design

Many game APIs are accessed via Harmony's `AccessTools.Field/Property/Method` rather than direct references. This avoids compile-time dependencies on game assemblies and survives API changes. The `SteamLobbyHelper` uses reflection to call Steamworks.NET without referencing the assembly.

### Godot Integration

The mod includes a `project.godot` and `export_presets.cfg` for Godot editor compatibility. The `RemoveMultiplayerPlayerLimit/` directory contains mod assets (cover image, localization JSONs) that get packed into the `.pck` via `build_pck.gd`. The Godot project is only used for building the PCK resource file — the mod logic is entirely in C#.
