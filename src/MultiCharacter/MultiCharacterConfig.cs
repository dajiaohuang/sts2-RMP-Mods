namespace RemoveMultiplayerPlayerLimit.MultiCharacter;

/// <summary>
/// Runtime configuration for the multi-character feature.
/// Persisted in config.ini under [multi_character] section.
/// </summary>
internal static class MultiCharacterConfig
{
	internal const bool DefaultEnabled = true;

	internal static bool Enabled { get; private set; } = DefaultEnabled;

	internal static void SetEnabled(bool value)
	{
		Enabled = value;
	}
}
