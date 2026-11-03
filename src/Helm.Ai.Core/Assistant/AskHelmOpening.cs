using Helm.Core.Settings;

namespace Helm.Ai.Assistant;

/// <summary>What Ask Helm's card does first when it opens.</summary>
public enum AskHelmOpenMode
{
    /// <summary>Listens at once (the Quick Settings tile, the edge handle, the widget's mic, the "Ask Helm" icon).</summary>
    Listen,

    /// <summary>The typing field, with the keyboard (the widget's Aa).</summary>
    Type,

    /// <summary>The card's camera (the widget's camera button).</summary>
    Camera,
}

/// <summary>
/// The intent extra that tells Ask Helm's card how to open (<see cref="AskHelmOpenMode"/>): a string, "listen", "type"
/// or "camera". Missing or unknown: it listens, as it always did.
/// </summary>
public static class AskHelmOpen
{
    public const string ExtraOpen = "com.huyhung1404.helm.extra.ASK_OPEN";

    public const string Listen = "listen";
    public const string Type = "type";
    public const string Camera = "camera";

    public static string Value(AskHelmOpenMode mode) => mode switch
    {
        AskHelmOpenMode.Type => Type,
        AskHelmOpenMode.Camera => Camera,
        _ => Listen,
    };

    public static AskHelmOpenMode Parse(string? value) => value switch
    {
        Type => AskHelmOpenMode.Type,
        Camera => AskHelmOpenMode.Camera,
        _ => AskHelmOpenMode.Listen,
    };
}

/// <summary>The screen edge the edge handle sits on.</summary>
public enum EdgeSide
{
    Right,
    Left,
}

/// <summary>
/// The other ways to open Ask Helm on the phone (Quick Capture › Ways to open), stored in settings/ask-helm-open.json on
/// that phone only: the edge handle (a thin line on the screen's edge, over the other apps) and the separate "Ask Helm"
/// launcher icon. The home-screen widget needs no setting.
/// </summary>
public sealed class AskHelmOpenSettings : IVersionedSettings
{
    public const string StoreId = "ask-helm-open";

    /// <summary>
    /// The handle's height. At most 160dp: Android lets an app keep at most 200dp of each edge from the Back gesture,
    /// and the handle must keep all of its own.
    /// </summary>
    public const int MinHeightDp = 48;
    public const int MaxHeightDp = 160;
    public const int HeightStepDp = 8;

    /// <summary>How far down the screen the handle's middle sits, in percent of the room it has.</summary>
    public const int MinPositionPercent = 5;
    public const int MaxPositionPercent = 95;

    public static int CurrentVersion => 1;

    public int Version { get; set; }

    /// <summary>Off until the user turns it on: it draws over other apps, which they allow first.</summary>
    public bool EdgeHandle { get; set; }

    public EdgeSide EdgeSide { get; set; } = EdgeSide.Right;

    public int EdgeHeightDp { get; set; } = 96;

    public int EdgePositionPercent { get; set; } = 45;

    /// <summary>Hidden while the app in front is full screen (games, videos).</summary>
    public bool HideOverFullScreen { get; set; } = true;

    /// <summary>The separate "Ask Helm" icon in the launcher (RedMagic's Game Space slider can open it).</summary>
    public bool LauncherIcon { get; set; } = true;

    /// <summary>A height the handle can have: within 48–160dp, on the 8dp steps of the slider.</summary>
    public static int ClampHeight(int dp)
    {
        var clamped = Math.Clamp(dp, MinHeightDp, MaxHeightDp);
        return MinHeightDp + (int)Math.Round((clamped - MinHeightDp) / (double)HeightStepDp) * HeightStepDp;
    }

    public static int ClampPosition(int percent) => Math.Clamp(percent, MinPositionPercent, MaxPositionPercent);
}
