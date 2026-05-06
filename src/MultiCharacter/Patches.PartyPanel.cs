using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using RemoveMultiplayerPlayerLimit.MultiCharacter;

namespace RemoveMultiplayerPlayerLimit;

public static partial class ModEntry
{
	// ── In-Run Party Panel Patches ───────────────────────────────────────────

	private static Node? _partyPanel;
	private static readonly List<Control> PartyPanelFrames = new();
	private static int _lastKnownActiveSlot = -1;

	/// <summary>
	/// Discover and patch the party panel (left-side character list in run screen).
	/// </summary>
	[HarmonyPatch]
	private static class PartyPanelReadyPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			Type? panelType = FindPartyPanelType();
			if (panelType == null) yield break;
			MethodInfo? ready = AccessTools.Method(panelType, "_Ready");
			if (ready != null) yield return ready;
		}

		private static void Postfix(Node __instance)
		{
			if (!MultiCharacterConfig.Enabled) return;
			_partyPanel = __instance;

			try
			{
				Log.Info($"PartyPanel found: {ReflectionDiscovery.GetNodeTypeHierarchy(__instance)}");
				InjectPartyPanelModifications(__instance);
				CharacterOwnershipManager.OwnershipChanged += OnOwnershipChanged;
				CharacterOwnershipManager.ActiveSlotChanged += OnActiveSlotChanged;
				CharacterOwnershipManager.StateCleared += OnStateCleared;
			}
			catch (Exception ex)
			{
				Log.Warn($"Failed to inject party panel mod: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Patches the party panel's update/per-frame method to keep the
	/// character order and active highlight current.
	/// </summary>
	[HarmonyPatch]
	private static class PartyPanelProcessPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			Type? panelType = FindPartyPanelType();
			if (panelType == null) yield break;

			foreach (string name in new[] { "_Process", "_PhysicsProcess", "UpdateParty", "Refresh", "UpdateDisplay" })
			{
				MethodInfo? method = AccessTools.Method(panelType, name);
				if (method != null)
				{
					Log.Info($"PartyPanel: patching update method: {name}");
					yield return method;
					yield break;
				}
			}
		}

		private static void Postfix()
		{
			if (!MultiCharacterConfig.Enabled || _partyPanel == null) return;
			RefreshCharacterOrder();
			RefreshActiveHighlight();
		}
	}

	/// <summary>
	/// Inject the grouping frame and ensure click handlers on character entries.
	/// </summary>
	private static void InjectPartyPanelModifications(Node panel)
	{
		// 1. Create a "Your Characters" group frame
		ColorRect frame = new ColorRect();
		frame.Name = "RmpOwnedGroupFrame";
		frame.Color = new Color(0.3f, 0.6f, 1.0f, 0.08f);
		frame.MouseFilter = Control.MouseFilterEnum.Ignore;
		frame.Visible = false;
		PartyPanelFrames.Add(frame);

		Label groupLabel = new Label();
		groupLabel.Name = "RmpOwnedGroupLabel";
		groupLabel.Text = GetLocalizedText("MULTI_CHAR_YOUR_CHARACTERS", "Your Characters");
		groupLabel.Visible = false;
		groupLabel.AddThemeColorOverride("font_color", new Color(0.5f, 0.8f, 1.0f, 1f));
		groupLabel.AddThemeFontSizeOverride("font_size", 12);
		groupLabel.HorizontalAlignment = HorizontalAlignment.Left;
		groupLabel.MouseFilter = Control.MouseFilterEnum.Ignore;

		if (panel is Control panelControl)
		{
			panelControl.AddChild(frame);
			panelControl.AddChild(groupLabel);
		}
		else
		{
			panel.AddChild(frame);
			panel.AddChild(groupLabel);
		}

		// 2. Find character entry nodes and attach click handlers
		List<Node> charEntries = FindCharacterEntries(panel);
		Log.Info($"PartyPanel: found {charEntries.Count} character entry nodes");

		foreach (Node entry in charEntries)
		{
			if (entry is Control control)
			{
				// Add click handler for switching active character
				control.GuiInput += (InputEvent evt) =>
				{
					if (evt is InputEventMouseButton mouseEvt
						&& mouseEvt.ButtonIndex == MouseButton.Left
						&& mouseEvt.Pressed)
					{
						HandleCharacterEntryClick(control);
					}
				};
			}
		}
	}

	/// <summary>
	/// Handle a click on a character entry in the party panel.
	/// Switches the local player's active character to the clicked entry's slot.
	/// </summary>
	private static void HandleCharacterEntryClick(Control entry)
	{
		if (!MultiCharacterConfig.Enabled) return;

		// Determine which slot this entry represents
		int slotIndex = entry.GetIndex();
		List<Node> charEntries = _partyPanel != null ? FindCharacterEntries(_partyPanel) : new List<Node>();
		slotIndex = charEntries.IndexOf(entry);
		if (slotIndex < 0) return;

		// Check if the local player owns this character
		Player? me = RunManager.Instance.State != null
			? LocalContext.GetMe(RunManager.Instance.State.Players)
			: null;
		if (me == null) return;

		ulong? owner = CharacterOwnershipManager.GetSlotOwner(slotIndex);
		if (owner != me.NetId) return; // Don't switch to other players' characters

		// Perform the switch
		CharacterOwnershipManager.SetActiveSlot(me.NetId, slotIndex);
		RmpOwnershipProtocol.BroadcastActiveSwitch(slotIndex);
		RefreshActiveHighlight();
	}

	/// <summary>
	/// Refresh the ordering of character entries: owned characters first, grouped together.
	/// </summary>
	private static void RefreshCharacterOrder()
	{
		if (_partyPanel == null) return;

		Player? me = RunManager.Instance.State != null
			? LocalContext.GetMe(RunManager.Instance.State.Players)
			: null;
		if (me == null) return;

		List<Node> charEntries = FindCharacterEntries(_partyPanel);
		if (charEntries.Count == 0) return;

		List<int> ownedSlots = CharacterOwnershipManager.GetSlotsForPlayer(me.NetId);
		if (ownedSlots.Count <= 1) return; // No reordering needed for single character

		bool anyFrameVisible = false;

		// Reorder: owned slots first, then others in original order
		for (int i = 0; i < ownedSlots.Count && i < charEntries.Count; i++)
		{
			int ownedSlot = ownedSlots[i];
			if (ownedSlot >= charEntries.Count) continue;

			Node entry = charEntries[ownedSlot];
			entry.GetParent()?.MoveChild(entry, i);
			anyFrameVisible = true;
		}

		// Update frame visibility and position
		foreach (Control frame in PartyPanelFrames)
		{
			if (frame.Name == "RmpOwnedGroupFrame" && anyFrameVisible && ownedSlots.Count > 1)
			{
				frame.Visible = true;
				// Position frame around owned entries
				if (charEntries.Count > 0 && charEntries[0] is Control firstEntry)
				{
					float top = firstEntry.Position.Y;
					float bottom = top + firstEntry.Size.Y * ownedSlots.Count;
					frame.Position = new Vector2(firstEntry.Position.X - 4, top - 4);
					frame.Size = new Vector2(firstEntry.Size.X + 8, bottom - top + 8);
				}
			}
		}

		// Update label
		Label? label = _partyPanel.GetNodeOrNull<Label>("RmpOwnedGroupLabel");
		if (label != null)
		{
			label.Visible = anyFrameVisible && ownedSlots.Count > 1;
		}
	}

	/// <summary>
	/// Refresh the visual highlight on the currently active character.
	/// </summary>
	private static void RefreshActiveHighlight()
	{
		if (_partyPanel == null) return;

		Player? me = RunManager.Instance.State != null
			? LocalContext.GetMe(RunManager.Instance.State.Players)
			: null;
		if (me == null) return;

		int activeSlot = CharacterOwnershipManager.GetActiveSlot(me.NetId);
		if (activeSlot == _lastKnownActiveSlot) return;
		_lastKnownActiveSlot = activeSlot;

		List<Node> charEntries = FindCharacterEntries(_partyPanel);

		for (int i = 0; i < charEntries.Count; i++)
		{
			if (charEntries[i] is Control control)
			{
				if (i == activeSlot)
				{
					// Highlight active character
					control.Modulate = new Color(1f, 1f, 0.8f, 1f);
					// Add a subtle border effect via a child ColorRect
					EnsureActiveBorder(control, visible: true);
				}
				else
				{
					// Reset non-active
					control.Modulate = Colors.White;
					EnsureActiveBorder(control, visible: false);
				}
			}
		}
	}

	private static void EnsureActiveBorder(Control target, bool visible)
	{
		ColorRect? border = target.GetNodeOrNull<ColorRect>("RmpActiveBorder");
		if (border == null && visible)
		{
			border = new ColorRect();
			border.Name = "RmpActiveBorder";
			border.Color = new Color(1f, 0.9f, 0.3f, 0.4f);
			border.MouseFilter = Control.MouseFilterEnum.Ignore;
			border.Size = target.Size;
			target.AddChild(border);
		}
		if (border != null)
			border.Visible = visible;
	}

	// ── Event Handlers ───────────────────────────────────────────────────────

	private static void OnOwnershipChanged(int slotIndex, ulong newOwner)
	{
		RefreshCharacterOrder();
	}

	private static void OnActiveSlotChanged(ulong playerNetId, int newSlot)
	{
		RefreshActiveHighlight();
	}

	private static void OnStateCleared()
	{
		_partyPanel = null;
		PartyPanelFrames.Clear();
		_lastKnownActiveSlot = -1;
	}

	// ── Discovery Helpers ────────────────────────────────────────────────────

	/// <summary>
	/// Find the party panel (in-run left sidebar showing all characters).
	/// </summary>
	private static Type? FindPartyPanelType()
	{
		string[] patterns = { "PartyPanel", "PartyHUD", "PlayerPanel", "NHUD",
			"NParty", "NRunHUD", "NCharacterBar", "NSideBar", "NTeamPanel" };

		foreach (string pattern in patterns)
		{
			List<Type> matches = ReflectionDiscovery.FindTypesByName(pattern);
			Type? match = matches.FirstOrDefault(t =>
				typeof(Node).IsAssignableFrom(t) && !t.IsAbstract);
			if (match != null)
			{
				Log.Info($"PartyPanel: found type via pattern '{pattern}': {match.FullName}");
				return match;
			}
		}
		Log.Warn("PartyPanel: could not find party panel type.");
		return null;
	}

	/// <summary>
	/// Find individual character entry nodes within the party panel.
	/// These are the clickable nodes representing each character.
	/// </summary>
	private static List<Node> FindCharacterEntries(Node panel)
	{
		List<Node> results = new();

		string[] patterns = { "Character", "PlayerEntry", "PartyMember",
			"HeroEntry", "PlayerSlot", "HP", "Block", "PlayerIcon" };

		// Walk children to find the container that holds character entries
		// (typically a VBoxContainer or HBoxContainer)
		Node? entryContainer = null;
		foreach (Node child in panel.GetChildren())
		{
			if (child is Container)
			{
				entryContainer = child;
				break;
			}
		}

		Node searchRoot = entryContainer ?? panel;
		foreach (Node child in searchRoot.GetChildren())
		{
			foreach (string pattern in patterns)
			{
				if (child.Name.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase))
				{
					results.Add(child);
					break;
				}
			}
			// Also include direct children of containers
			if (results.Count == 0 && child is Container container)
			{
				foreach (Node grandchild in container.GetChildren())
				{
					results.Add(grandchild);
				}
			}
		}

		// Fallback: if no pattern match, return all Control children
		if (results.Count == 0)
		{
			foreach (Node child in searchRoot.GetChildren())
			{
				if (child is Control)
					results.Add(child);
			}
		}

		return results;
	}
}
