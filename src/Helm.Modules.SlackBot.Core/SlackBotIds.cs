namespace Helm.Modules.SlackBot;

/// <summary>Slack Report's ids, for the code that does not know the Windows module.</summary>
public static class SlackBotIds
{
    /// <summary>The module's id: its settings file, its data folder and the MCP tools. Still "slackbot" from its old name.</summary>
    public const string ModuleId = "slackbot";
}

/// <summary>
/// What Slack Report's view models ask of the app around them (on Windows: the shell's navigation and the list on
/// screen). Resolved late by the app, so the view models do not depend on its pages or windows.
/// </summary>
public interface ISlackBotShell
{
    /// <summary>Slack Report's page, in front (from the list on screen, or the old settings page).</summary>
    void ShowPage();

    /// <summary>The list on screen back in its corner, unfolded.</summary>
    void ResetOverlay();
}
