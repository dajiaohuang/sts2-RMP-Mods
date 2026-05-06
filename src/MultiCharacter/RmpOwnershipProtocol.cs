using System;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RemoveMultiplayerPlayerLimit.MultiCharacter;

/// <summary>
/// Protocol handler for multi-character ownership messages.
/// Extends the existing RMP protocol channel with ownership-specific message handling.
///
/// Host responsibilities:
///   - Handle claim requests from clients
///   - Broadcast ownership state on changes
///
/// Client responsibilities:
///   - Receive and apply ownership sync from host
///   - Receive active switch notifications
/// </summary>
internal static class RmpOwnershipProtocol
{
    private static INetGameService? _netService;

    internal static bool IsActive => _netService != null;

    internal static void RegisterHandlers(INetGameService netService)
    {
        UnregisterHandlers();
        _netService = netService;

        netService.RegisterMessageHandler<RmpOwnershipSyncMessage>(HandleOwnershipSync);
        netService.RegisterMessageHandler<RmpClaimSlotMessage>(HandleClaimSlot);
        netService.RegisterMessageHandler<RmpActiveSwitchMessage>(HandleActiveSwitch);

        Log.Info($"RMP ownership protocol registered on {netService.Type}");
    }

    internal static void UnregisterHandlers()
    {
        if (_netService == null) return;

        try
        {
            _netService.UnregisterMessageHandler<RmpOwnershipSyncMessage>(HandleOwnershipSync);
            _netService.UnregisterMessageHandler<RmpClaimSlotMessage>(HandleClaimSlot);
            _netService.UnregisterMessageHandler<RmpActiveSwitchMessage>(HandleActiveSwitch);
        }
        catch (Exception)
        {
            // Service may be disposed
        }
        _netService = null;
    }

    /// <summary>
    /// Host broadcasts current ownership state to all clients.
    /// Call after any ownership change.
    /// </summary>
    internal static void BroadcastOwnership()
    {
        if (_netService == null || _netService.Type != NetGameType.Host) return;

        var (entryCount, entries, activeCount, actives) = CharacterOwnershipManager.GetSnapshot();

        _netService.SendMessage(new RmpOwnershipSyncMessage
        {
            TotalSlots = CharacterOwnershipManager.SlotCount,
            OwnerEntries = entries,
            ActiveEntries = actives
        });
    }

    /// <summary>
    /// Client sends claim request to host.
    /// </summary>
    internal static void SendClaimRequest(int slotIndex)
    {
        if (_netService == null) return;

        _netService.SendMessage(new RmpClaimSlotMessage { SlotIndex = slotIndex });
    }

    /// <summary>
    /// Notify all clients that a player switched their active character.
    /// </summary>
    internal static void BroadcastActiveSwitch(int slotIndex)
    {
        if (_netService == null) return;

        _netService.SendMessage(new RmpActiveSwitchMessage { SlotIndex = slotIndex });
    }

    // ── Message Handlers ─────────────────────────────────────────────────────

    private static void HandleOwnershipSync(RmpOwnershipSyncMessage message, ulong senderId)
    {
        if (_netService?.Type == NetGameType.Host) return; // Host doesn't receive its own broadcasts

        Log.Info($"RMP ownership sync received from host: {message}");

        if (message.OwnerEntries == null || message.ActiveEntries == null) return;

        CharacterOwnershipManager.RestoreFromSnapshot(
            message.OwnerEntries,
            message.ActiveEntries,
            message.TotalSlots);
    }

    private static void HandleClaimSlot(RmpClaimSlotMessage message, ulong senderId)
    {
        if (_netService?.Type != NetGameType.Host) return;

        Log.Info($"RMP claim slot request: slot={message.SlotIndex} from player {senderId}");

        // Host validates: slot must be unowned
        if (!CharacterOwnershipManager.IsSlotUnowned(message.SlotIndex))
        {
            Log.Warn($"RMP: player {senderId} tried to claim already-owned slot {message.SlotIndex}");
            return;
        }

        CharacterOwnershipManager.AssignSlot(message.SlotIndex, senderId);
        BroadcastOwnership();
    }

    private static void HandleActiveSwitch(RmpActiveSwitchMessage message, ulong senderId)
    {
        CharacterOwnershipManager.SetActiveSlot(senderId, message.SlotIndex);

        if (_netService?.Type == NetGameType.Host)
        {
            // Host re-broadcasts so all clients see the switch
            _netService.SendMessage(new RmpActiveSwitchMessage { SlotIndex = message.SlotIndex });
        }
    }
}
