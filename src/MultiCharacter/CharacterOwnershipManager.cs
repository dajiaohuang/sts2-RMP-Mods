using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;

namespace RemoveMultiplayerPlayerLimit.MultiCharacter;

/// <summary>
/// Central authority for tracking which player (NetId) controls which character slots.
/// Host-authoritative: only the host modifies ownership. Clients receive sync broadcasts.
///
/// Slot index = position in the global character list (same ordering as LobbyPlayer slotId).
/// Owner NetId = the Steam/network ID of the human player controlling that character.
/// NetId 0 = unowned slot (character has no controlling player).
/// </summary>
internal static class CharacterOwnershipManager
{
    /// <summary>slotIndex → ownerNetId (0 = unowned)</summary>
    private static readonly Dictionary<int, ulong> SlotOwnership = new();

    /// <summary>playerNetId → activeSlotIndex (which character they're currently controlling)</summary>
    private static readonly Dictionary<ulong, int> ActiveSlotPerPlayer = new();

    /// <summary>Unowned slot indices (for UI display and assignment).</summary>
    private static readonly HashSet<int> UnownedSlots = new();

    /// <summary>Total number of known character slots.</summary>
    internal static int SlotCount { get; private set; }

    internal static event Action<int, ulong>? OwnershipChanged;
    internal static event Action<ulong, int>? ActiveSlotChanged;
    internal static event Action? StateCleared;

    // ── Ownership ───────────────────────────────────────────────────────────

    internal static void Initialize(int totalSlots)
    {
        Clear();
        SlotCount = totalSlots;
        for (int i = 0; i < totalSlots; i++)
        {
            SlotOwnership[i] = 0;
            UnownedSlots.Add(i);
        }
    }

    internal static void AssignSlot(int slotIndex, ulong playerNetId)
    {
        if (slotIndex < 0 || slotIndex >= SlotCount)
        {
            Log.Warn($"CharacterOwnershipManager: slot {slotIndex} out of range (0-{SlotCount - 1})");
            return;
        }

        ulong previousOwner = SlotOwnership.GetValueOrDefault(slotIndex, 0UL);
        SlotOwnership[slotIndex] = playerNetId;

        if (playerNetId == 0)
            UnownedSlots.Add(slotIndex);
        else
            UnownedSlots.Remove(slotIndex);

        // Auto-set active slot for player's first character
        if (playerNetId != 0 && (!ActiveSlotPerPlayer.ContainsKey(playerNetId) || ActiveSlotPerPlayer[playerNetId] < 0))
            ActiveSlotPerPlayer[playerNetId] = slotIndex;

        Log.Info($"CharacterOwnershipManager: slot {slotIndex} assigned to player {playerNetId}");
        OwnershipChanged?.Invoke(slotIndex, playerNetId);
    }

    internal static ulong? GetSlotOwner(int slotIndex)
    {
        return SlotOwnership.TryGetValue(slotIndex, out ulong owner) && owner != 0
            ? owner
            : null;
    }

    internal static bool IsSlotOwned(int slotIndex)
    {
        return SlotOwnership.TryGetValue(slotIndex, out ulong owner) && owner != 0;
    }

    internal static bool IsSlotUnowned(int slotIndex)
    {
        return UnownedSlots.Contains(slotIndex);
    }

    internal static IReadOnlyList<int> GetUnownedSlots()
    {
        return UnownedSlots.OrderBy(i => i).ToList();
    }

    internal static List<int> GetSlotsForPlayer(ulong playerNetId)
    {
        return SlotOwnership
            .Where(kv => kv.Value == playerNetId)
            .Select(kv => kv.Key)
            .OrderBy(i => i)
            .ToList();
    }

    internal static int GetOwnedSlotCount(ulong playerNetId)
    {
        return SlotOwnership.Count(kv => kv.Value == playerNetId);
    }

    internal static bool PlayerHasMultipleCharacters(ulong playerNetId)
    {
        return GetOwnedSlotCount(playerNetId) > 1;
    }

    // ── Active Slot (character switching) ────────────────────────────────────

    internal static void SetActiveSlot(ulong playerNetId, int slotIndex)
    {
        if (SlotOwnership.TryGetValue(slotIndex, out ulong owner) && owner != playerNetId)
        {
            Log.Warn($"CharacterOwnershipManager: player {playerNetId} cannot activate slot {slotIndex} (owned by {owner})");
            return;
        }

        int previous = ActiveSlotPerPlayer.GetValueOrDefault(playerNetId, -1);
        ActiveSlotPerPlayer[playerNetId] = slotIndex;

        Log.Info($"CharacterOwnershipManager: player {playerNetId} switched active slot {previous} → {slotIndex}");
        ActiveSlotChanged?.Invoke(playerNetId, slotIndex);
    }

    internal static int GetActiveSlot(ulong playerNetId)
    {
        return ActiveSlotPerPlayer.TryGetValue(playerNetId, out int slot) ? slot : -1;
    }

    internal static IReadOnlyDictionary<ulong, int> GetAllActiveSlots()
    {
        return new Dictionary<ulong, int>(ActiveSlotPerPlayer);
    }

    // ── Legacy Save Mapping ──────────────────────────────────────────────────

    /// <summary>
    /// Generate ownership mapping from a legacy save that has no ownership data.
    /// Each slot gets its original player's NetId. Players no longer connected
    /// (not in connectedNetIds) have their slots marked as unowned (0).
    /// </summary>
    internal static void InitializeLegacyMapping(
        IReadOnlyList<ulong> slotNetIds,
        IReadOnlySet<ulong> connectedNetIds,
        int totalSlots)
    {
        Clear();
        SlotCount = totalSlots;

        for (int i = 0; i < totalSlots; i++)
        {
            ulong netId = i < slotNetIds.Count ? slotNetIds[i] : 0;
            if (netId != 0 && connectedNetIds.Contains(netId))
            {
                SlotOwnership[i] = netId;
            }
            else
            {
                SlotOwnership[i] = 0;
                UnownedSlots.Add(i);
            }

            if (netId != 0 && !ActiveSlotPerPlayer.ContainsKey(netId))
                ActiveSlotPerPlayer[netId] = i;
        }

        Log.Info($"CharacterOwnershipManager: legacy mapping initialized. " +
            $"Owned: {SlotCount - UnownedSlots.Count}, Unowned: {UnownedSlots.Count}");
    }

    // ── Serialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Serialize ownership state into a binary-friendly format (entry count + entries).
    /// Each entry: [4 bits slotIndex][64 bits netId]
    /// Total entries count written as 8 bits.
    /// Active slot entries: [8 bits count][entries: 64 bits netId + 4 bits slot]
    /// </summary>
    internal static (int EntryCount, List<(int slotIndex, ulong netId)> Entries,
        int ActiveCount, List<(ulong netId, int slot)> Actives) GetSnapshot()
    {
        List<(int slotIndex, ulong netId)> entries = SlotOwnership
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(e => e.Key)
            .ToList();

        List<(ulong netId, int slot)> actives = ActiveSlotPerPlayer
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(e => e.Key)
            .ToList();

        return (entries.Count, entries, actives.Count, actives);
    }

    /// <summary>
    /// Restore ownership state from a snapshot.
    /// </summary>
    internal static void RestoreFromSnapshot(
        List<(int slotIndex, ulong netId)> entries,
        List<(ulong netId, int slot)> actives,
        int totalSlots)
    {
        Clear();
        SlotCount = totalSlots;
        foreach (var (slotIndex, netId) in entries)
        {
            SlotOwnership[slotIndex] = netId;
            if (netId == 0)
                UnownedSlots.Add(slotIndex);
        }
        foreach (var (netId, slot) in actives)
        {
            ActiveSlotPerPlayer[netId] = slot;
        }
        Log.Info($"CharacterOwnershipManager: restored snapshot ({entries.Count} slots, {actives.Count} active)");
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    internal static void Clear()
    {
        SlotOwnership.Clear();
        ActiveSlotPerPlayer.Clear();
        UnownedSlots.Clear();
        SlotCount = 0;
        StateCleared?.Invoke();
    }

    internal static void LogState()
    {
        Log.Info("=== CharacterOwnershipManager State ===");
        Log.Info($"  SlotCount: {SlotCount}, Unowned: {UnownedSlots.Count}");

        foreach (var kv in SlotOwnership.OrderBy(kv => kv.Key))
        {
            string status = kv.Value == 0 ? "UNOWNED" : $"player {kv.Value}";
            int? active = ActiveSlotPerPlayer.TryGetValue(kv.Value, out int a) && a == kv.Key ? a : null;
            string marker = active.HasValue ? " [ACTIVE]" : "";
            Log.Info($"  Slot {kv.Key}: {status}{marker}");
        }
    }
}
