using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Shell.Services;

namespace Helm.Ai;

/// <summary>One saved key as the AI page lists it. Never holds the secret, only its mask.</summary>
public sealed partial class AiKeyRow(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _masked = "";
    [ObservableProperty] private string _remoteName = "";
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private AiKeyStatus? _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _checkedText = "";
    [ObservableProperty] private IReadOnlyList<AiModel> _models = [];
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public bool Works => Status == AiKeyStatus.Works;
    public bool IsBlocked => Status == AiKeyStatus.Blocked;
    public bool IsRejected => Status == AiKeyStatus.Rejected;
    public bool IsUnknown => Status is AiKeyStatus.Unknown or null;
    public bool HasModels => Models.Count > 0;
    public string ModelsText => Models.Count == 1 ? "1 model" : $"{Models.Count} models";

    partial void OnStatusChanged(AiKeyStatus? value)
    {
        OnPropertyChanged(nameof(Works));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsRejected));
        OnPropertyChanged(nameof(IsUnknown));
    }

    partial void OnModelsChanged(IReadOnlyList<AiModel> value)
    {
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ModelsText));
    }

    internal void Show(AiKey key)
    {
        Label = key.Label;
        Masked = AiKeyStore.Mask(key.Secret);
        RemoteName = key.LastCheck?.RemoteName is { Length: > 0 } remote && remote != key.Label ? $"“{remote}” in the console" : "";
        IsDefault = key.IsDefault;
        Status = key.LastCheck?.Status;
        StatusText = key.LastCheck?.Message ?? "Not tested on any device yet.";
        CheckedText = key.LastCheck is { } check ? $"Tested {AiKeysViewModel.When(check.At)}" : "";
        Models = key.LastCheck?.Models ?? [];
    }
}

/// <summary>One provider: its keys, and its prepaid balance where its API tells it.</summary>
public sealed partial class AiProviderSection(AiProvider provider, bool readsBalance) : ObservableObject
{
    public AiProvider Provider { get; } = provider;
    public string Title => Provider.Name;
    public string Subtitle => $"{Provider.Company} · keys start with {Provider.KeyPrefix}";
    public string EmptyText => $"No {Provider.Name} key yet.";
    public ObservableCollection<AiKeyRow> Keys { get; } = [];
    public bool HasKeys => Keys.Count > 0;

    /// <summary>The provider's API tells the prepaid balance (with a management key).</summary>
    public bool ReadsBalance { get; } = readsBalance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddManagementKey))]
    private bool _hasManagementKey;

    [ObservableProperty] private string _balanceText = "";
    [ObservableProperty] private string _balanceDetail = "";
    [ObservableProperty] private bool _isLowBalance;
    [ObservableProperty] private bool _isReadingBalance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddManagementKey))]
    private bool _isAddingManagementKey;
    [ObservableProperty] private string _newManagementKey = "";
    [ObservableProperty] private string _managementProblem = "";

    public bool CanAddManagementKey => !HasManagementKey && !IsAddingManagementKey;

    partial void OnNewManagementKeyChanged(string value) => ManagementProblem = "";

    internal void KeysChanged() => OnPropertyChanged(nameof(HasKeys));
}

/// <summary>
/// The Models &amp; keys part of the AI page: the API keys Helm's tools call AI models with (Claude, Grok…), on every
/// device, and what each provider's API tells about them. Shared by both apps; the views only bind to it.
/// </summary>
public sealed partial class AiKeysViewModel : ObservableObject
{
    /// <summary>Below this the balance is shown as running low.</summary>
    internal const decimal LowBalanceUsd = 2m;

    private readonly AiKeyStore _store;
    private readonly AiManagementKeys _managementKeys;
    private readonly IReadOnlyDictionary<string, IAiKeyChecker> _checkers;
    private readonly IReadOnlyDictionary<string, IAiBalanceReader> _readers;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private AiProvider _newProvider = AiProviders.Claude;
    [ObservableProperty] private string _newLabel = "";
    [ObservableProperty] private string _newSecret = "";
    [ObservableProperty] private string _newProblem = "";
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _message = "";

    public AiKeysViewModel(AiKeyStore store, AiManagementKeys managementKeys, IEnumerable<IAiKeyChecker> checkers,
        IEnumerable<IAiBalanceReader> readers, IUiDispatcher ui, IProcessLauncher launcher, IDialogService dialogs)
    {
        _store = store;
        _managementKeys = managementKeys;
        _checkers = checkers.ToDictionary(c => c.Provider.Id);
        _readers = readers.ToDictionary(r => r.Provider.Id);
        _ui = ui;
        _launcher = launcher;
        _dialogs = dialogs;
        Sections = [.. AiProviders.All.Select(p => new AiProviderSection(p, _readers.ContainsKey(p.Id))
        {
            HasManagementKey = _managementKeys.Has(p),
        })];
        _store.Changed += (_, _) => _ui.Post(Refresh);
        // Another device added or forgot a management key.
        _managementKeys.Changed += (_, _) => _ui.Post(() =>
        {
            foreach (var section in Sections) section.HasManagementKey = _managementKeys.Has(section.Provider);
        });
        Refresh();
    }

    public IReadOnlyList<AiProviderSection> Sections { get; }

    public IReadOnlyList<AiProvider> Providers => AiProviders.All;

    public bool HasMessage => Message.Length > 0;

    /// <summary>Raised after a key was saved, so the view can clear its password box.</summary>
    public event EventHandler? NewKeySaved;

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnNewSecretChanged(string value) => NewProblem = "";

    partial void OnNewProviderChanged(AiProvider value) => NewProblem = "";

    internal static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date ? $"today, {local:HH:mm}" : local.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Rebuilds the rows from the store, keeping what is going on in a row (a test, a rename).</summary>
    public void Refresh()
    {
        var keys = _store.All();
        foreach (var section in Sections)
        {
            var mine = keys.Where(k => k.Value.Provider == section.Provider.Id).ToList();
            var old = section.Keys.ToDictionary(r => r.Id);
            section.Keys.Clear();
            foreach (var key in mine)
            {
                var row = old.GetValueOrDefault(key.Id) ?? new AiKeyRow(key.Id);
                row.Show(key.Value);
                section.Keys.Add(row);
            }
            section.KeysChanged();
        }
    }

    /// <summary>The page came into view: read the balances that can be read.</summary>
    public Task OnShownAsync() =>
        Task.WhenAll(Sections.Where(s => s.ReadsBalance && s.HasManagementKey).Select(ReadBalanceAsync));

    [RelayCommand]
    private void StartAdd(AiProvider? provider)
    {
        NewProvider = provider ?? AiProviders.Claude;
        NewLabel = "";
        NewSecret = "";
        NewProblem = "";
        Message = "";
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        NewSecret = "";
        NewProblem = "";
    }

    /// <summary>Tests the key first: a key the provider rejects is not saved; any other answer is saved with it.</summary>
    [RelayCommand]
    private async Task SaveNew()
    {
        if (IsSaving) return;
        var provider = NewProvider;
        var secret = NewSecret.Trim();
        if (AiKeyStore.Problem(provider, secret) is { } problem)
        {
            NewProblem = problem;
            return;
        }
        if (_store.Contains(secret))
        {
            NewProblem = "This key is already saved.";
            return;
        }
        IsSaving = true;
        try
        {
            var check = await _checkers[provider.Id].CheckAsync(secret, CancellationToken.None);
            if (check.Status == AiKeyStatus.Rejected)
            {
                NewProblem = check.Message;
                return;
            }
            var label = NewLabel.Trim().Length > 0 ? NewLabel.Trim() : check.RemoteName ?? provider.Name;
            _store.Add(provider, label, secret, check);
            IsAdding = false;
            NewSecret = "";
            Message = check.Status == AiKeyStatus.Works
                ? $"Saved “{label}”. It syncs to your other devices, end-to-end encrypted."
                : $"Saved “{label}”, but: {check.Message}";
            NewKeySaved?.Invoke(this, EventArgs.Empty);
            if (Sections.First(s => s.Provider == provider) is { ReadsBalance: true, HasManagementKey: true } section)
                await ReadBalanceAsync(section);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task Test(AiKeyRow? row)
    {
        if (row is null || row.IsTesting || _store.Get(row.Id) is not { } key || !_checkers.TryGetValue(key.Provider, out var checker)) return;
        row.IsTesting = true;
        try
        {
            var check = await checker.CheckAsync(key.Secret, CancellationToken.None);
            _store.SaveCheck(row.Id, check);
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    [RelayCommand]
    private void MakeDefault(AiKeyRow? row)
    {
        if (row is not null) _store.MakeDefault(row.Id);
    }

    [RelayCommand]
    private void StartRename(AiKeyRow? row)
    {
        if (row is null) return;
        row.RenameText = row.Label;
        row.IsRenaming = true;
    }

    [RelayCommand]
    private void SaveRename(AiKeyRow? row)
    {
        if (row is null) return;
        _store.Rename(row.Id, row.RenameText);
        row.IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename(AiKeyRow? row)
    {
        if (row is not null) row.IsRenaming = false;
    }

    [RelayCommand]
    private async Task Delete(AiKeyRow? row)
    {
        if (row is null) return;
        var ok = await _dialogs.ConfirmAsync($"Remove “{row.Label}”?",
            "Helm forgets this key on every device, and the tools that use it stop until you add another. The key itself keeps working: to stop it for good, delete it in the provider's console too.",
            "Remove");
        if (!ok) return;
        _store.Delete(row.Id);
        Message = $"Removed “{row.Label}”.";
    }

    [RelayCommand]
    private void StartManagementKey(AiProviderSection? section)
    {
        if (section is null) return;
        section.NewManagementKey = "";
        section.ManagementProblem = "";
        section.IsAddingManagementKey = true;
    }

    [RelayCommand]
    private void CancelManagementKey(AiProviderSection? section)
    {
        if (section is null) return;
        section.IsAddingManagementKey = false;
        section.NewManagementKey = "";
    }

    /// <summary>Reads the balance with the new key first, and keeps the key only when the provider accepts it.</summary>
    [RelayCommand]
    private async Task SaveManagementKey(AiProviderSection? section)
    {
        if (section is null || section.IsReadingBalance) return;
        var key = section.NewManagementKey.Trim();
        if (key.Length == 0 || key.Any(char.IsWhiteSpace))
        {
            section.ManagementProblem = key.Length == 0 ? "Paste the management key." : "A key has no spaces or line breaks: copy it again.";
            return;
        }
        if (Account(section.Provider) is not { } account)
        {
            section.ManagementProblem = $"Add and test a {section.Provider.Name} key first: Helm reads the balance of its team.";
            return;
        }
        section.IsReadingBalance = true;
        try
        {
            var balance = await _readers[section.Provider.Id].ReadAsync(key, account, CancellationToken.None);
            if (balance.Usd is null)
            {
                section.ManagementProblem = balance.Message;
                return;
            }
            _managementKeys.Save(section.Provider, key);
            section.HasManagementKey = true;
            section.IsAddingManagementKey = false;
            section.NewManagementKey = "";
            Show(section, balance);
        }
        finally
        {
            section.IsReadingBalance = false;
        }
    }

    [RelayCommand]
    private async Task RemoveManagementKey(AiProviderSection? section)
    {
        if (section is null) return;
        var ok = await _dialogs.ConfirmAsync("Forget the management key?",
            $"Helm stops reading the {section.Provider.Name} balance on every device. The key itself keeps working: delete it in the console to stop it for good.",
            "Forget");
        if (!ok) return;
        _managementKeys.Delete(section.Provider);
        section.HasManagementKey = false;
        section.BalanceText = "";
        section.BalanceDetail = "";
        section.IsLowBalance = false;
    }

    [RelayCommand]
    private Task RefreshBalance(AiProviderSection? section) => section is null ? Task.CompletedTask : ReadBalanceAsync(section);

    [RelayCommand]
    private void DismissMessage() => Message = "";

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url)) _launcher.OpenUrl(url);
    }

    private async Task ReadBalanceAsync(AiProviderSection section)
    {
        if (section.IsReadingBalance || !_readers.TryGetValue(section.Provider.Id, out var reader)) return;
        if (_managementKeys.Get(section.Provider) is not { } key)
        {
            section.HasManagementKey = false;
            section.BalanceDetail = "";
            return;
        }
        if (Account(section.Provider) is not { } account)
        {
            section.BalanceText = "";
            section.BalanceDetail = $"Test a {section.Provider.Name} key: Helm reads the balance of its team.";
            return;
        }
        section.IsReadingBalance = true;
        try
        {
            Show(section, await reader.ReadAsync(key, account, CancellationToken.None));
        }
        finally
        {
            section.IsReadingBalance = false;
        }
    }

    private static void Show(AiProviderSection section, AiBalance balance)
    {
        if (balance.Usd is { } usd)
        {
            section.BalanceText = usd.ToString("$#,0.00", CultureInfo.InvariantCulture);
            section.IsLowBalance = usd < LowBalanceUsd;
            section.BalanceDetail = $"{balance.Message} Read {When(balance.At)}.";
        }
        else
        {
            section.BalanceDetail = balance.Message;
        }
    }

    /// <summary>The account a provider's balance is read for: the one the default key's last test found.</summary>
    private string? Account(AiProvider provider) =>
        _store.DefaultFor(provider)?.Value.LastCheck?.Account
        ?? _store.All().Where(k => k.Value.Provider == provider.Id).Select(k => k.Value.LastCheck?.Account).FirstOrDefault(a => a is not null);
}
