namespace zRover.BackgroundManager.Packages;

/// <summary>
/// Represents a single launchable app entry within an MSIX package.
/// One package may contain multiple app entries (e.g. a main app + a background task host).
/// </summary>
public sealed class AppEntryInfo
{
    /// <summary>The in-package app ID (e.g. "App").</summary>
    public required string AppId { get; init; }

    /// <summary>
    /// The Application User Model ID, used for activation.
    /// Format: <c>{packageFamilyName}!{appId}</c>
    /// </summary>
    public required string Aumid { get; init; }

    /// <summary>Display name of this app entry, localised if available.</summary>
    public required string DisplayName { get; init; }
}
