namespace Helm.Modules.Vocab;

/// <summary>What the phone looks like when the pop-up card or the bubble is about to show.</summary>
public sealed record VocabOverlayState
{
    /// <summary>Vocabulary is turned on.</summary>
    public bool ModuleOn { get; init; } = true;

    /// <summary>Helm may draw over other apps.</summary>
    public bool CanDraw { get; init; } = true;

    /// <summary>The screen is on and unlocked: someone is there to read it.</summary>
    public bool InUse { get; init; } = true;

    public bool Landscape { get; init; }

    /// <summary>The app in front (package name); null when unknown (no usage access, or nothing found).</summary>
    public string? ForegroundApp { get; init; }

    /// <summary>Helm's own package: over Helm the user is already in the app.</summary>
    public string? OwnApp { get; init; }

    public IReadOnlyCollection<string> BlockedApps { get; init; } = [];

    /// <summary>A word is ready to ask (the card only; the bubble shows without one).</summary>
    public bool HasWord { get; init; } = true;
}

/// <summary>Why the pop-up or the bubble stays hidden; <see cref="Show"/> when nothing keeps it.</summary>
public enum VocabOverlayVerdict
{
    Show,
    Off,
    NoPermission,
    NotInUse,
    Landscape,
    InHelm,
    BlockedApp,
    NoWord,
    DismissedUntilUnlock,
}

/// <summary>
/// When the phone's pop-up card and floating bubble may show. Never sideways (games and videos usually are), never over
/// an app the user blocked, never over Helm itself, and only while someone is using the phone. Pure, so it is tested
/// without a phone; the Android side gathers the <see cref="VocabOverlayState"/>.
/// </summary>
public static class VocabOverlayRules
{
    /// <summary>Whether the scheduled card shows now.</summary>
    public static VocabOverlayVerdict Card(VocabOverlayState state, bool popupEnabled) =>
        Common(state, popupEnabled) is var verdict && verdict != VocabOverlayVerdict.Show ? verdict
        : !state.HasWord ? VocabOverlayVerdict.NoWord
        : VocabOverlayVerdict.Show;

    /// <summary>Whether the bubble is on screen now.</summary>
    public static VocabOverlayVerdict Bubble(VocabOverlayState state, bool bubbleEnabled, bool dismissedUntilUnlock) =>
        Common(state, bubbleEnabled) is var verdict && verdict != VocabOverlayVerdict.Show ? verdict
        : dismissedUntilUnlock ? VocabOverlayVerdict.DismissedUntilUnlock
        : VocabOverlayVerdict.Show;

    /// <summary>Whether the app in front is one the user keeps the pop-up out of.</summary>
    public static bool IsBlocked(string? app, IReadOnlyCollection<string> blocked) =>
        app is not null && blocked.Contains(app, StringComparer.Ordinal);

    private static VocabOverlayVerdict Common(VocabOverlayState state, bool enabled)
    {
        if (!state.ModuleOn || !enabled) return VocabOverlayVerdict.Off;
        if (!state.CanDraw) return VocabOverlayVerdict.NoPermission;
        if (!state.InUse) return VocabOverlayVerdict.NotInUse;
        if (state.Landscape) return VocabOverlayVerdict.Landscape;
        if (state.OwnApp is not null && state.ForegroundApp == state.OwnApp) return VocabOverlayVerdict.InHelm;
        if (IsBlocked(state.ForegroundApp, state.BlockedApps)) return VocabOverlayVerdict.BlockedApp;
        return VocabOverlayVerdict.Show;
    }
}
