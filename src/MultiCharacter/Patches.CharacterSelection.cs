using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using RemoveMultiplayerPlayerLimit.MultiCharacter;

namespace RemoveMultiplayerPlayerLimit;

public static partial class ModEntry
{
	// ── Character Selection Multi-Select Patches ─────────────────────────────

	/// <summary>
	/// Tracks the character select screen node so other patches can find it.
	/// </summary>
	private static Node? _characterSelectScreen;

	/// <summary>
	/// Tracks which character slots the local player has toggled for selection.
	/// Key = slot index, Value = selection order (1-based, 0 = unselected).
	/// </summary>
	private static readonly Dictionary<int, int> LocalCharacterSelections = new();

	/// <summary>
	/// Discover and patch the character selection screen _Ready method.
	/// Uses runtime type discovery to find the screen regardless of its exact name.
	/// </summary>
	[HarmonyPatch]
	private static class CharacterSelectScreenReadyPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			Type? screenType = FindCharacterSelectScreenType();
			if (screenType == null) yield break;
			MethodInfo? ready = AccessTools.Method(screenType, "_Ready");
			if (ready != null) yield return ready;
		}

		private static void Postfix(Node __instance)
		{
			if (!MultiCharacterConfig.Enabled) return;
			_characterSelectScreen = __instance;

			try
			{
				Log.Info($"CharacterSelectScreen found: {ReflectionDiscovery.GetNodeTypeHierarchy(__instance)}");
				InjectMultiSelectUI(__instance);
			}
			catch (Exception ex)
			{
				Log.Warn($"Failed to inject multi-select UI: {ex.Message}");
			}
		}
	}

	/// <summary>
	/// Discover and patch the method that handles character portrait click / selection.
	/// This changes single-select to toggle-based multi-select.
	/// </summary>
	[HarmonyPatch]
	private static class CharacterSelectClickPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			Type? screenType = FindCharacterSelectScreenType();
			if (screenType == null) yield break;

			// Look for methods likely handling character selection click
			foreach (MethodInfo method in screenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				string name = method.Name.ToLowerInvariant();
				if ((name.Contains("select") || name.Contains("click") || name.Contains("character") || name.Contains("pick"))
					&& method.GetParameters().Length >= 1
					&& (method.GetParameters()[0].ParameterType == typeof(int) ||
						method.GetParameters()[0].ParameterType.IsEnum))
				{
					Log.Info($"CharacterSelect: patching potential selection handler: {method.Name}");
					yield return method;
					yield break; // Patch only the first match
				}
			}
		}

		/// <summary>
		/// Prefix intercepts character selection. In multi-char mode, converts it to a toggle.
		/// The original method parameter is the character class/slot index being clicked.
		/// </summary>
		private static bool Prefix(object[] __args, MethodBase __originalMethod)
		{
			if (!MultiCharacterConfig.Enabled) return true;

			ParameterInfo[] parameters = __originalMethod.GetParameters();
			if (parameters.Length == 0) return true;

			// Try to extract the slot/character index from the first int-compatible parameter
			int slotIndex = -1;
			object firstArg = __args[0];
			if (firstArg is int i)
				slotIndex = i;
			else if (firstArg is Enum e)
				slotIndex = Convert.ToInt32(e);
			else
				return true; // Can't determine index, let original run

			// Toggle selection
			if (LocalCharacterSelections.ContainsKey(slotIndex) && LocalCharacterSelections[slotIndex] > 0)
			{
				LocalCharacterSelections.Remove(slotIndex);
				Log.Info($"CharacterSelect: deselected slot {slotIndex}");
			}
			else
			{
				int nextOrder = LocalCharacterSelections.Count > 0 ? LocalCharacterSelections.Values.Max() + 1 : 1;
				LocalCharacterSelections[slotIndex] = nextOrder;
				Log.Info($"CharacterSelect: selected slot {slotIndex} (order {nextOrder})");
			}

			UpdateMultiSelectVisuals();

			// Skip original handler — we handle selection in our own logic
			return false;
		}
	}

	/// <summary>
	/// Discover and patch the "confirm" / "ready" button on the character select screen.
	/// Overrides to send multi-character selection via the RMP protocol instead of standard single-select.
	/// </summary>
	[HarmonyPatch]
	private static class CharacterSelectConfirmPatch
	{
		private static IEnumerable<MethodBase> TargetMethods()
		{
			Type? screenType = FindCharacterSelectScreenType();
			if (screenType == null) yield break;

			foreach (MethodInfo method in screenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
			{
				string name = method.Name.ToLowerInvariant();
				if (name.Contains("confirm") || name.Contains("ready") || name.Contains("lock") || name.Contains("start"))
				{
					Log.Info($"CharacterSelect: patching potential confirm handler: {method.Name}");
					yield return method;
					yield break;
				}
			}
		}

		private static bool Prefix()
		{
			if (!MultiCharacterConfig.Enabled) return true;

			Log.Info($"CharacterSelect: confirming multi-char selection ({LocalCharacterSelections.Count} slots)");

			// Send claim messages for each selected slot
			foreach (var (slotIndex, _) in LocalCharacterSelections)
			{
				RmpOwnershipProtocol.SendClaimRequest(slotIndex);
			}

			// Clear local state
			LocalCharacterSelections.Clear();
			return true; // Let original confirm logic run (starts the game)
		}
	}

	/// <summary>
	/// Patch the lobby screen to show multi-character ownership info and
	/// allow reassigning unowned character slots.
	/// </summary>
	[HarmonyPatch(typeof(StartRunLobby), MethodType.Constructor,
		typeof(GameMode), typeof(INetGameService), typeof(IStartRunLobbyListener), typeof(int))]
	private static class LobbyMultiCharInitPatch
	{
		private static void Postfix(StartRunLobby __instance, INetGameService netService, int maxPlayers)
		{
			if (!MultiCharacterConfig.Enabled) return;

			// Initialize ownership manager with the lobby's player capacity
			if (netService.Type == NetGameType.Host)
			{
				CharacterOwnershipManager.Initialize(maxPlayers);
				Log.Info($"Multi-char: initialized ownership for {maxPlayers} slots (host)");
			}
		}
	}

	// ── Helper Methods ───────────────────────────────────────────────────────

	/// <summary>
	/// Find the character selection screen type by scanning loaded assemblies.
	/// Uses multiple name patterns to be resilient to game updates.
	/// </summary>
	private static Type? FindCharacterSelectScreenType()
	{
		string[] patterns = { "CharacterSelect", "CharSelect", "LobbyCharacterSelect",
			"PickCharacter", "HeroSelect", "ClassSelect", "NLobbySetup" };

		foreach (string pattern in patterns)
		{
			List<Type> matches = ReflectionDiscovery.FindTypesByName(pattern);
			Type? match = matches.FirstOrDefault(t =>
				typeof(Node).IsAssignableFrom(t) && !t.IsAbstract);
			if (match != null)
			{
				Log.Info($"CharacterSelect: found screen type via pattern '{pattern}': {match.FullName}");
				return match;
			}
		}
		Log.Warn("CharacterSelect: could not find character selection screen type.");
		return null;
	}

	/// <summary>
	/// Inject multi-select UI elements into the character selection screen.
	/// Adds "+" badges or checkmarks to character portraits and a multi-select indicator.
	/// </summary>
	private static void InjectMultiSelectUI(Node screen)
	{
		// Walk the child tree to find character portrait nodes
		// Character portraits are typically named "Character*" or "Hero*" or "Portrait*"
		List<Node> portraitNodes = FindNodesByNamePattern(screen, new[] { "Character", "Hero", "Portrait", "Char", "Slot" });

		Log.Info($"CharacterSelect: found {portraitNodes.Count} potential portrait nodes");

		// For each portrait, we'll add a "selection order" badge (a small Label)
		foreach (Node portrait in portraitNodes)
		{
			if (portrait is Control control)
			{
				// Create a number badge (initially hidden)
				Label badge = new Label();
				badge.Name = "RmpMultiSelectBadge";
				badge.Text = "";
				badge.Visible = false;
				badge.AddThemeColorOverride("font_color", Colors.White);
				badge.AddThemeFontSizeOverride("font_size", 16);
				badge.HorizontalAlignment = HorizontalAlignment.Center;
				badge.VerticalAlignment = VerticalAlignment.Center;
				badge.CustomMinimumSize = new Vector2(28, 28);
				badge.Position = new Vector2(-4, -4);
				control.AddChild(badge);
			}
		}
	}

	/// <summary>
	/// Update the visual badges on character portraits to reflect current selections.
	/// </summary>
	private static void UpdateMultiSelectVisuals()
	{
		if (_characterSelectScreen == null) return;

		List<Node> portraitNodes = FindNodesByNamePattern(_characterSelectScreen,
			new[] { "Character", "Hero", "Portrait", "Char", "Slot" });

		for (int i = 0; i < portraitNodes.Count; i++)
		{
			if (portraitNodes[i] is Control control)
			{
				Label? badge = control.GetNodeOrNull<Label>("RmpMultiSelectBadge");
				if (badge == null) continue;

				bool selected = LocalCharacterSelections.TryGetValue(i, out int order) && order > 0;
				badge.Visible = selected;
				badge.Text = selected ? order.ToString() : "";
			}
		}
	}

	/// <summary>
	/// Recursively find nodes whose names match any of the given patterns (case-insensitive).
	/// </summary>
	private static List<Node> FindNodesByNamePattern(Node root, string[] patterns)
	{
		List<Node> results = new();

		void Walk(Node node)
		{
			foreach (string pattern in patterns)
			{
				if (node.Name.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase))
				{
					results.Add(node);
					break;
				}
			}
			foreach (Node child in node.GetChildren())
			{
				Walk(child);
			}
		}

		Walk(root);
		return results;
	}
}
