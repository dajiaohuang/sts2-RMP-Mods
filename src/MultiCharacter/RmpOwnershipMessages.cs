using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace RemoveMultiplayerPlayerLimit.MultiCharacter;

/// <summary>
/// Host broadcasts full ownership state to all clients when it changes.
/// Packet format:
///   [8 bits]  OwnerEntryCount
///   for each: [4 bits] SlotIndex, [32 bits] NetIdHi, [32 bits] NetIdLo
///   [8 bits]  ActiveEntryCount
///   for each: [32 bits] NetIdHi, [32 bits] NetIdLo, [4 bits] ActiveSlotIndex
/// </summary>
public struct RmpOwnershipSyncMessage : INetMessage, IPacketSerializable
{
    public int TotalSlots;
    public List<(int slotIndex, ulong netId)>? OwnerEntries;
    public List<(ulong netId, int slot)>? ActiveEntries;

    public readonly bool ShouldBroadcast => true;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.Info;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(TotalSlots, 8);

        int ownerCount = OwnerEntries?.Count ?? 0;
        writer.WriteInt(ownerCount, 8);
        if (OwnerEntries != null)
        {
            foreach (var (slotIndex, netId) in OwnerEntries)
            {
                writer.WriteInt(slotIndex, 4);
                writer.WriteInt((int)(netId >> 32), 32);
                writer.WriteInt((int)(netId & 0xFFFFFFFF), 32);
            }
        }

        int activeCount = ActiveEntries?.Count ?? 0;
        writer.WriteInt(activeCount, 8);
        if (ActiveEntries != null)
        {
            foreach (var (netId, slot) in ActiveEntries)
            {
                writer.WriteInt((int)(netId >> 32), 32);
                writer.WriteInt((int)(netId & 0xFFFFFFFF), 32);
                writer.WriteInt(slot, 4);
            }
        }
    }

    public void Deserialize(PacketReader reader)
    {
        TotalSlots = reader.ReadInt(8);

        int ownerCount = reader.ReadInt(8);
        OwnerEntries = new List<(int, ulong)>(ownerCount);
        for (int i = 0; i < ownerCount; i++)
        {
            int slotIndex = reader.ReadInt(4);
            ulong hi = (ulong)(uint)reader.ReadInt(32);
            ulong lo = (ulong)(uint)reader.ReadInt(32);
            ulong netId = (hi << 32) | lo;
            OwnerEntries.Add((slotIndex, netId));
        }

        int activeCount = reader.ReadInt(8);
        ActiveEntries = new List<(ulong, int)>(activeCount);
        for (int i = 0; i < activeCount; i++)
        {
            ulong hi = (ulong)(uint)reader.ReadInt(32);
            ulong lo = (ulong)(uint)reader.ReadInt(32);
            ulong netId = (hi << 32) | lo;
            int slot = reader.ReadInt(4);
            ActiveEntries.Add((netId, slot));
        }
    }

    public override readonly string ToString()
    {
        return $"RmpOwnershipSync(totalSlots={TotalSlots}, owners={OwnerEntries?.Count ?? 0}, actives={ActiveEntries?.Count ?? 0})";
    }
}

/// <summary>
/// Client requests to claim an unowned character slot. Sent client→host.
/// </summary>
public struct RmpClaimSlotMessage : INetMessage, IPacketSerializable
{
    public int SlotIndex;

    public readonly bool ShouldBroadcast => false;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.Info;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(SlotIndex, 4);
    }

    public void Deserialize(PacketReader reader)
    {
        SlotIndex = reader.ReadInt(4);
    }

    public override readonly string ToString()
    {
        return $"RmpClaimSlot(slot={SlotIndex})";
    }
}

/// <summary>
/// Broadcast when a player switches which character they're actively controlling.
/// </summary>
public struct RmpActiveSwitchMessage : INetMessage, IPacketSerializable
{
    public int SlotIndex;

    public readonly bool ShouldBroadcast => true;
    public readonly NetTransferMode Mode => NetTransferMode.Reliable;
    public readonly LogLevel LogLevel => LogLevel.Info;

    public readonly void Serialize(PacketWriter writer)
    {
        writer.WriteInt(SlotIndex, 4);
    }

    public void Deserialize(PacketReader reader)
    {
        SlotIndex = reader.ReadInt(4);
    }

    public override readonly string ToString()
    {
        return $"RmpActiveSwitch(slot={SlotIndex})";
    }
}
