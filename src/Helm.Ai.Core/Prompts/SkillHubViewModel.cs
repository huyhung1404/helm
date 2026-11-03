using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.Ai.Prompts;

/// <summary>
/// The Skill Hub card of the AI page (PC): the hub's MCP address, this machine's token and the Cloudflare Access service
/// token in front of it, and what the last fetch of the features' prompts did. The tokens go straight into
/// <see cref="SkillHubSecrets"/> (encrypted for this user, this device only); the page never shows them again, only that
/// one is saved. UI thread only.
/// </summary>
public sealed partial class SkillHubViewModel : ObservableObject
{
    private readonly ISettingsStore<SkillHubSettings> _settings;
    private readonly SkillHubSecrets _secrets;
    private readonly AiPromptRefresher _refresher;
    private readonly AiPrompts _prompts;

    [ObservableProperty] private string _url = "";

    // What is typed, until Save: then cleared (the password boxes follow).
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string _accessClientId = "";
    [ObservableProperty] private string _accessClientSecret = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string _problem = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isOk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenHint))]
    private bool _hasToken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessHint))]
    private bool _hasAccess;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    public SkillHubViewModel(ISettingsStore<SkillHubSettings> settings, SkillHubSecrets secrets, AiPromptRefresher refresher, AiPrompts prompts,
        IUiDispatcher ui)
    {
        _settings = settings;
        _secrets = secrets;
        _refresher = refresher;
        _prompts = prompts;
        refresher.Fetched += (_, _) => ui.Post(ShowStatus);
        Load();
    }

    public bool HasProblem => Problem.Length > 0;

    public string TokenHint => HasToken ? "Saved · type a new one to replace it" : "The Skill Hub → Machines → New token";

    public string AccessHint => HasAccess ? "Saved · type new ones to replace them" : "Only when Cloudflare Access guards the hub";

    /// <summary>The page came into view: the address and the tokens as saved now (a token typed into the file moves out).</summary>
    public void Load()
    {
        _secrets.Connection(_settings);
        Url = _settings.Current.Url;
        (HasToken, HasAccess) = _secrets.Saved();
        Problem = "";
        ShowStatus();
    }

    /// <summary>
    /// Saves the address and the tokens typed (an empty box keeps the saved one), then fetches the prompts at once. An
    /// empty address turns the Skill Hub off: Helm's built-in prompts are used again within a few days.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var url = Url.Trim();
        if (url.Length > 0 && SkillHubSecrets.UrlProblem(url) is { } problem)
        {
            Problem = problem;
            return;
        }
        if (AccessClientId.Trim().Length > 0 != AccessClientSecret.Trim().Length > 0 && !HasAccess)
        {
            Problem = "The Cloudflare Access service token has two parts: type its client id and its secret.";
            return;
        }
        if (url.Length > 0 && !HasToken && Token.Trim().Length == 0)
        {
            Problem = "Type this machine's token: the Skill Hub → Machines → New token.";
            return;
        }
        Problem = "";
        _secrets.Save(Token, AccessClientId, AccessClientSecret);
        _settings.Update(s => s.Url = url);
        _settings.Flush();
        Token = AccessClientId = AccessClientSecret = "";
        (HasToken, HasAccess) = _secrets.Saved();
        if (url.Length == 0)
        {
            ShowStatus();
            return;
        }
        IsSaving = true;
        StatusText = "Fetching the prompts…";
        try
        {
            await _refresher.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // Saved all the same; the next fetch (every 15 minutes) tries again.
            Problem = "Saved, but the prompts could not be fetched: " + ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
        ShowStatus();
    }

    private bool CanSave() => !IsSaving;

    /// <summary>
    /// "Fetched today, 09:12 · 1 prompt in use: helm-ask-helm", or why the last fetch failed, or that it is not connected.
    /// </summary>
    private void ShowStatus()
    {
        var inUse = _prompts.InUse();
        var used = inUse.Count switch
        {
            0 => "no prompt of yours in use (save one named helm-ask-helm)",
            1 => $"1 prompt in use: {inUse[0]}",
            _ => $"{inUse.Count} prompts in use: {string.Join(", ", inUse)}",
        };
        if (_settings.Current.Url.Trim().Length == 0 || !HasToken)
        {
            IsOk = false;
            StatusText = inUse.Count > 0 ? $"Not connected · {used} until it is too old" : "Not connected: Helm uses its built-in prompts.";
            return;
        }
        if (_refresher.Problem is { } problem)
        {
            IsOk = false;
            StatusText = $"{problem.TrimEnd('.')} · Helm keeps the prompts it has ({used}).";
            return;
        }
        IsOk = _refresher.LastFetched is not null;
        StatusText = _refresher.LastFetched is { } at
            ? $"Fetched {When(at)} · {used}"
            : "Connected · the prompts are fetched every 15 minutes.";
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date ? $"today, {local:HH:mm}" : local.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
    }
}
