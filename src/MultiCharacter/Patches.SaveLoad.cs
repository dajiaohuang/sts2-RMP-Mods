using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using RemoveMultiplayerPlayerLimit.MultiCharacter;

namespace RemoveMultiplayerPlayerLimit;

public static partial class ModEntry
{
	// ── Save/Load Ownership Persistence Patches ──────────────────────────────

	private static Type? _saveDataType;
	private static MethodInfo? _saveSerializeMethod;
	private static MethodInfo? _saveDeserializeMethod;
	private static MethodInfo? _runLoadMethod;

	/// <summary>
	/// Discover save/load types and patch their serialize/deserialize methods.
	/// </summary>
	[HarmonyPatch]
	private static class SaveSerializePatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			DiscoverSaveSystem();
			if (_saveSerializeMethod != null) yield return _saveSerializeMethod;
		}

		private static void Postfix(object __instance, PacketWriter writer)
		{
			if (!MultiCharacterConfig.Enabled) return;
			WriteOwnershipToSave(writer);
		}
	}

	[HarmonyPatch]
	private static class SaveDeserializePatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			DiscoverSaveSystem();
			if (_saveDeserializeMethod != null) yield return _saveDeserializeMethod;
		}

		private static void Postfix(object __instance, PacketReader reader)
		{
			if (!MultiCharacterConfig.Enabled) return;
			ReadOwnershipFromSave(reader);
		}
	}

	/// <summary>
	/// Patch the save data constructor or the method that initializes a new blank save.
	/// Used to detect legacy saves (no ownership data) and initialize defaults.
	/// </summary>
	[HarmonyPatch]
	private static class SaveNewOrLoadPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			DiscoverSaveSystem();
			if (_runLoadMethod != null) yield return _runLoadMethod;
		}

		private static void Postfix()
		{
			if (!MultiCharacterConfig.Enabled) return;
			DetectAndHandleLegacySave();
		}
	}

	// ── Ownership Serialization ──────────────────────────────────────────────

	/// <summary>
	/// Serialize ownership data into the game's save data via PacketWriter.
	/// Format:
	///   [8 bits]  magic marker (0xAA) — indicates ownership data present
	///   [8 bits]  totalSlots
	///   [8 bits]  entryCount
	///   for each: [4 bits] slotIndex, [32 bits] netIdHi, [32 bits] netIdLo
	///   [8 bits]  activeEntryCount
	///   for each: [32 bits] netIdHi, [32 bits] netIdLo, [4 bits] activeSlot
	/// </summary>
	private static void WriteOwnershipToSave(PacketWriter writer)
	{
		writer.WriteInt(0xAA, 8); // Magic marker

		var (entryCount, entries, activeCount, actives) = CharacterOwnershipManager.GetSnapshot();

		writer.WriteInt(CharacterOwnershipManager.SlotCount, 8);
		writer.WriteInt(entryCount, 8);

		foreach (var (slotIndex, netId) in entries)
		{
			writer.WriteInt(slotIndex, 4);
			writer.WriteInt((int)(netId >> 32), 32);
			writer.WriteInt((int)(netId & 0xFFFFFFFF), 32);
		}

		writer.WriteInt(activeCount, 8);
		foreach (var (netId, slot) in actives)
		{
			writer.WriteInt((int)(netId >> 32), 32);
			writer.WriteInt((int)(netId & 0xFFFFFFFF), 32);
			writer.WriteInt(slot, 4);
		}
	}

	/// <summary>
	/// Read ownership data from a save. If the magic marker is not present,
	/// this is a legacy save and InitializeLegacyMapping should be called instead.
	/// </summary>
	private static void ReadOwnershipFromSave(PacketReader reader)
	{
		// We can't easily "peek" in PacketReader, so we rely on the save format
		// being structured. The magic marker check is done in DetectAndHandleLegacySave.
		// Here we just attempt to deserialize if multi-char is enabled.
		try
		{
			int marker = reader.ReadInt(8);
			if (marker != 0xAA)
			{
				Log.Info("SaveLoad: no ownership data marker found — legacy save detected.");
				return;
			}

			int totalSlots = reader.ReadInt(8);
			int ownerCount = reader.ReadInt(8);
			var ownerEntries = new List<(int, ulong)>(ownerCount);

			for (int i = 0; i < ownerCount; i++)
			{
				int slotIndex = reader.ReadInt(4);
				ulong hi = (ulong)(uint)reader.ReadInt(32);
				ulong lo = (ulong)(uint)reader.ReadInt(32);
				ownerEntries.Add((slotIndex, (hi << 32) | lo));
			}

			int activeCount = reader.ReadInt(8);
			var activeEntries = new List<(ulong, int)>(activeCount);

			for (int i = 0; i < activeCount; i++)
			{
				ulong hi = (ulong)(uint)reader.ReadInt(32);
				ulong lo = (ulong)(uint)reader.ReadInt(32);
				ulong netId = (hi << 32) | lo;
				int slot = reader.ReadInt(4);
				activeEntries.Add((netId, slot));
			}

			CharacterOwnershipManager.RestoreFromSnapshot(ownerEntries, activeEntries, totalSlots);
			Log.Info($"SaveLoad: restored ownership from save ({ownerCount} slots, {activeCount} active)");
		}
		catch (Exception ex)
		{
			Log.Warn($"SaveLoad: failed to read ownership data (may be legacy save): {ex.Message}");
		}
	}

	/// <summary>
	/// After a save/run is loaded, check if this is a legacy save
	/// and initialize default ownership mapping.
	/// </summary>
	private static void DetectAndHandleLegacySave()
	{
		// If ownership was already restored from the save, SlotCount will be > 0
		if (CharacterOwnershipManager.SlotCount > 0) return;

		IRunState? state = RunManager.Instance.State;
		if (state == null) return;

		IReadOnlyList<Player> players = state.Players;
		if (players.Count == 0) return;

		Log.Info($"SaveLoad: detected legacy save with {players.Count} players. Initializing ownership mapping.");

		// Gather connected player NetIds from the active lobby
		HashSet<ulong> connectedNetIds = new();
		// We can't easily access the lobby from here, so we use the state players directly
		// In a legacy save, all saved players existed — we just don't know who is currently connected.
		// For now, assign all players as owned. The host can reassign if needed.
		foreach (Player player in players)
		{
			connectedNetIds.Add(player.NetId);
		}

		List<ulong> slotNetIds = players.Select(p => p.NetId).ToList();
		CharacterOwnershipManager.InitializeLegacyMapping(slotNetIds, connectedNetIds, players.Count);
	}

	// ── Discovery ────────────────────────────────────────────────────────────

	private static void DiscoverSaveSystem()
	{
		if (_saveDataType != null) return; // Already discovered

		// Look for save data types
		string[] savePatterns = { "SaveData", "RunSaveData", "GameSaveData",
			"RunSave", "SaveFile", "PersistentData", "StateSave" };

		foreach (string pattern in savePatterns)
		{
			List<Type> matches = ReflectionDiscovery.FindTypesByName(pattern);
			_saveDataType = matches.FirstOrDefault(t =>
				!t.IsAbstract &&
				(t.GetMethod("Serialize") != null || t.GetMethod("Write") != null));

			if (_saveDataType != null)
			{
				Log.Info($"SaveLoad: found save data type: {_saveDataType.FullName}");
				break;
			}
		}

		if (_saveDataType != null)
		{
			_saveSerializeMethod = _saveDataType.GetMethod("Serialize")
				?? _saveDataType.GetMethod("Write");
			_saveDeserializeMethod = _saveDataType.GetMethod("Deserialize")
				?? _saveDataType.GetMethod("Read");
		}

		// Find the method that loads a save / starts a run
		Type? runManagerType = typeof(RunManager);
		_runLoadMethod = AccessTools.Method(runManagerType, "LoadRun")
			?? AccessTools.Method(runManagerType, "LoadState")
			?? AccessTools.Method(runManagerType, "LoadSave");

		if (_saveDataType == null)
		{
			Log.Warn("SaveLoad: could not discover save data type. Ownership will not persist in saves.");
		}
	}
}
