using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;

namespace Helm.Modules.Vault.ViewModels;

/// <summary>One field of the item being shown or edited. Secret values stay hidden until revealed.</summary>
public sealed partial class FieldViewModel(ItemDetailViewModel owner, VaultField field) : ObservableObject
{
    [ObservableProperty] private string _name = field.Name;
    [ObservableProperty] private string _value = field.Value;
    [ObservableProperty] private VaultFieldKind _kind = field.Kind;
    [ObservableProperty] private bool _isRevealed;

    public bool IsSecret => Kind is VaultFieldKind.Secret or VaultFieldKind.Password;

    public bool IsPassword => Kind == VaultFieldKind.Password;

    public bool IsMultiline => Kind == VaultFieldKind.Multiline;

    public bool IsLink => Kind == VaultFieldKind.Url && Uri.TryCreate(Value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    /// <summary>What the view shows when not editing: dots for a hidden secret.</summary>
    public string Display => Value.Length == 0 ? "—" : IsSecret && !IsRevealed ? "••••••••••" : Value;

    public bool HasValue => Value.Length > 0;

    public ItemDetailViewModel Owner { get; } = owner;

    public VaultField ToField() => new(Name.Trim(), Value, Kind);

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(HasValue));
    }

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(Display));

    partial void OnKindChanged(VaultFieldKind value)
    {
        OnPropertyChanged(nameof(IsSecret));
        OnPropertyChanged(nameof(IsPassword));
        OnPropertyChanged(nameof(IsMultiline));
        OnPropertyChanged(nameof(Display));
    }

    [RelayCommand]
    private void ToggleReveal()
    {
        IsRevealed = !IsRevealed;
        Owner.Touch();
    }

    [RelayCommand]
    private void Copy() => Owner.CopyField(this);

    [RelayCommand]
    private void Generate()
    {
        Value = PasswordGenerator.Generate(Owner.GeneratorOptions);
        IsRevealed = true;
    }

    [RelayCommand]
    private void Remove() => Owner.Fields.Remove(this);
}

public sealed partial class AttachmentViewModel(ItemDetailViewModel owner, VaultAttachment attachment, string status) : ObservableObject
{
    public VaultAttachment Attachment { get; } = attachment;

    public string Name => Attachment.Name;

    public string Details => Sizes.Join(Sizes.Format(Attachment.Size), status);

    [RelayCommand]
    private Task OpenAsync() => owner.OpenAttachmentAsync(this);

    [RelayCommand]
    private Task SaveAsAsync() => owner.SaveAttachmentAsync(this);

    [RelayCommand]
    private Task RemoveAsync() => owner.RemoveAttachmentAsync(this);
}

public sealed partial class VersionViewModel(ItemDetailViewModel owner, VaultItemVersion version) : ObservableObject
{
    public VaultItemVersion Version { get; } = version;

    public string When => DateTimeOffset.FromUnixTimeMilliseconds(Version.SavedAtMs).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Which fields differ from the current item (names only; values are never listed).</summary>
    public string Summary { get; } = owner.DescribeDifference(version.Item);

    [RelayCommand]
    private Task RestoreAsync() => owner.RestoreVersionAsync(this);
}

public sealed partial class ConflictViewModel(ItemDetailViewModel owner, VaultConflict conflict) : ObservableObject
{
    public VaultConflict Conflict { get; } = conflict;

    public string When => Conflict.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Summary { get; } = owner.DescribeDifference(conflict.Item);

    [RelayCommand]
    private Task KeepThisAsync() => owner.KeepConflictAsync(this);
}

/// <summary>
/// One item, shown or edited. Editing works on a copy: nothing reaches the store until Save, and Save keeps the old
/// version in the history.
/// </summary>
public sealed partial class ItemDetailViewModel : ObservableObject
{
    private readonly VaultStore _store;
    private readonly VaultFiles _files;
    private readonly VaultSession _session;
    private readonly IVaultPlatform _platform;
    private readonly Action<string?> _saved;
    private VaultItem _item;
    private string? _recordId;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private byte[]? _icon;

    /// <summary>
    /// Login, Token or Info, changeable while editing (e.g. a token that was saved as a login). The fields stay as they
    /// are; only the tab and the card's look depend on it.
    /// </summary>
    [ObservableProperty] private VaultItemType _type;

    internal ItemDetailViewModel(VaultStore store, VaultFiles files, VaultSession session, IVaultPlatform platform,
        VaultEntry? entry, VaultItemKind kind, Action<string?> saved)
    {
        _store = store;
        _files = files;
        _session = session;
        _platform = platform;
        _saved = saved;
        Uid = entry?.Uid;
        _recordId = entry?.RecordId;
        Trashed = entry?.Trashed ?? false;
        TrashedAt = entry?.TrashedAt;
        _item = entry?.Item ?? VaultItem.New(kind);
        foreach (var conflict in entry?.Conflicts ?? []) Conflicts.Add(new ConflictViewModel(this, conflict));
        Load(_item);
        IsEditing = entry is null;
    }

    /// <summary>Null for an item that is not saved yet.</summary>
    public string? Uid { get; private set; }

    public IReadOnlyList<VaultItemType> Types { get; } = Enum.GetValues<VaultItemType>();

    public bool IsInfo => Type == VaultItemType.Info;

    /// <summary>"Description" for Info items (the card shows it), "Notes" otherwise.</summary>
    public string NotesLabel => IsInfo ? "Description" : "Notes";

    partial void OnTypeChanged(VaultItemType value)
    {
        OnPropertyChanged(nameof(IsInfo));
        OnPropertyChanged(nameof(NotesLabel));
    }

    /// <summary>
    /// The stored kind for a type. Info keeps an older item's own kind (card, identity, document), so nothing is
    /// rewritten needlessly and older Helm versions still read it; a new Info item is stored as a note.
    /// </summary>
    private static VaultItemKind KindFor(VaultItemType type, VaultItemKind current) => type switch
    {
        VaultItemType.Login => VaultItemKind.Login,
        VaultItemType.Token => VaultItemKind.Token,
        _ => VaultCardViewModel.TypeOf(current) == VaultItemType.Info ? current : VaultItemKind.Note,
    };

    public bool Trashed { get; }

    public DateTimeOffset? TrashedAt { get; }

    public bool IsNew => Uid is null;

    public bool HasConflicts => Conflicts.Count > 0;

    public ObservableCollection<FieldViewModel> Fields { get; } = [];

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public ObservableCollection<VersionViewModel> History { get; } = [];

    public ObservableCollection<ConflictViewModel> Conflicts { get; } = [];

    public PasswordOptions GeneratorOptions { get; set; } = new();

    public string Modified => _item.ModifiedAtMs > 0
        ? "Modified " + DateTimeOffset.FromUnixTimeMilliseconds(_item.ModifiedAtMs).ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "";

    public IReadOnlyList<VaultFieldKind> FieldKinds { get; } = Enum.GetValues<VaultFieldKind>();

    public bool HasIcon => Icon is { Length: > 0 };

    public string Initial => VaultAvatar.Initial(Title);

    public string AvatarColor => VaultAvatar.Color(Title);

    partial void OnIconChanged(byte[]? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(AvatarColor));
    }

    /// <summary>A picture for the item (a logo you saved, a photo): PNG, JPEG or WebP up to 256 KB.</summary>
    [RelayCommand]
    private async Task ChooseIconAsync()
    {
        var picked = await _platform.PickFileAsync(CancellationToken.None).ConfigureAwait(true);
        if (picked is null) return;
        await using (picked.Content)
        {
            using var memory = new MemoryStream();
            await picked.Content.CopyToAsync(memory).ConfigureAwait(true);
            var bytes = memory.ToArray();
            if (bytes.Length > VaultItem.MaxIconBytes)
            {
                Error = "Choose a smaller picture (at most 256 KB). A square logo of 128 × 128 pixels is plenty.";
                return;
            }
            if (!LooksLikeImage(bytes))
            {
                Error = "That is not a PNG, JPEG or WebP picture.";
                return;
            }
            Error = null;
            Icon = bytes;
        }
        Touch();
    }

    [RelayCommand]
    private void RemoveIcon()
    {
        Icon = null;
        Touch();
    }

    private static bool LooksLikeImage(byte[] b) =>
        b.Length > 12 && ((b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)          // PNG
            || (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)                                     // JPEG
            || (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)); // WebP

    internal void Touch() => _session.Touch();

    internal void CopyField(FieldViewModel field)
    {
        if (field.IsSecret) _platform.CopySecret(field.Value);
        else _platform.CopyText(field.Value);
        Touch();
    }

    [RelayCommand]
    private void Edit()
    {
        if (Trashed) return;
        IsEditing = true;
        Touch();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsNew)
        {
            _saved(null);
            return;
        }
        Load(_item);
        IsEditing = false;
        Error = null;
    }

    [RelayCommand]
    private void Save()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Error = "Give the item a title.";
            return;
        }
        try
        {
            var edited = _item with
            {
                Kind = KindFor(Type, _item.Kind),
                Title = Title.Trim(),
                Notes = Notes,
                Favorite = Favorite,
                Icon = Icon,
                Tags = Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToList(),
                Fields = Fields.Select(f => f.ToField()).Where(f => f.Name.Length > 0 || f.Value.Length > 0).ToList(),
            };
            if (Uid is null) Uid = _store.Add(edited);
            else _store.Save(Uid, edited);
            _item = _store.Get(Uid)?.Item ?? edited;
            Load(_item);
            IsEditing = false;
            _saved(Uid);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Crypto.VaultKeyException or ArgumentException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void AddField(VaultFieldKind kind)
    {
        Fields.Add(new FieldViewModel(this, new VaultField(kind switch
        {
            VaultFieldKind.Password => "Password",
            VaultFieldKind.Secret => "Secret",
            VaultFieldKind.Url => "Website",
            VaultFieldKind.Email => "Email",
            VaultFieldKind.Phone => "Phone",
            VaultFieldKind.Username => "Username",
            _ => "Field",
        }, "", kind)));
        Touch();
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (Uid is null)
        {
            Save();
            if (Uid is null) return;
        }
        var picked = await _platform.PickFileAsync(CancellationToken.None).ConfigureAwait(true);
        if (picked is null) return;
        await RunAsync(async () =>
        {
            await using (picked.Content)
            {
                var progress = new Progress<double>(p => Progress = p);
                await _files.AttachAsync(Uid!, picked.Name, picked.MediaType, picked.Content, progress).ConfigureAwait(true);
            }
            Reload();
        }).ConfigureAwait(true);
    }

    internal Task OpenAttachmentAsync(AttachmentViewModel attachment) => RunAsync(async () =>
    {
        var data = await _files.ReadAllAsync(attachment.Attachment).ConfigureAwait(true);
        await _platform.OpenFileAsync(attachment.Name, attachment.Attachment.MediaType, data, CancellationToken.None).ConfigureAwait(true);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
    });

    internal Task SaveAttachmentAsync(AttachmentViewModel attachment) => RunAsync(async () =>
    {
        var stream = await _platform.CreateFileAsync(attachment.Name, attachment.Attachment.MediaType, CancellationToken.None).ConfigureAwait(true);
        if (stream is null) return;
        await using (stream) await _files.OpenAsync(attachment.Attachment, stream, new Progress<double>(p => Progress = p)).ConfigureAwait(true);
    });

    internal async Task RemoveAttachmentAsync(AttachmentViewModel attachment)
    {
        if (Uid is null) return;
        if (!await _platform.ConfirmAsync("Remove document", $"Remove {attachment.Name} from this item? It stays in the item's history, so you can restore it.", "Remove").ConfigureAwait(true))
            return;
        await RunAsync(() =>
        {
            _files.Remove(Uid, attachment.Attachment.Id);
            Reload();
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    internal async Task RestoreVersionAsync(VersionViewModel version)
    {
        if (Uid is null) return;
        if (!await _platform.ConfirmAsync("Restore this version", $"Replace the item with the version from {version.When}? The current version goes into the history.", "Restore").ConfigureAwait(true))
            return;
        await RunAsync(() =>
        {
            _store.RestoreVersion(Uid, version.Version);
            Reload();
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    internal async Task KeepConflictAsync(ConflictViewModel conflict)
    {
        if (Uid is null) return;
        if (!await _platform.ConfirmAsync("Keep this version", "Keep this version of the item? The other versions stay in its history.", "Keep").ConfigureAwait(true))
            return;
        await RunAsync(() =>
        {
            _store.ResolveConflict(Uid, conflict.Conflict.RecordId);
            _saved(Uid);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    /// <summary>Keeps the version shown as the item (the other copies go into its history).</summary>
    [RelayCommand]
    private Task KeepCurrentAsync() => Uid is null || _recordId is null ? Task.CompletedTask : RunAsync(() =>
    {
        _store.ResolveConflict(Uid, _recordId);
        _saved(Uid);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task MoveToTrashAsync() => Uid is null ? Task.CompletedTask : RunAsync(() =>
    {
        _store.MoveToTrash(Uid);
        _saved(null);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RestoreFromTrashAsync() => Uid is null ? Task.CompletedTask : RunAsync(() =>
    {
        _store.RestoreFromTrash(Uid);
        _saved(Uid);
        return Task.CompletedTask;
    });

    [RelayCommand]
    private async Task DeleteForeverAsync()
    {
        if (Uid is null) return;
        if (!await _platform.ConfirmAsync("Delete for good", $"Delete \"{Title}\" and its documents from every device? Your backups still have it.", "Delete").ConfigureAwait(true))
            return;
        await RunAsync(() =>
        {
            _store.Purge(Uid);
            _saved(null);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    internal string DescribeDifference(VaultItem other)
    {
        var changes = new List<string>();
        if (other.Title != _item.Title) changes.Add("title");
        var names = other.Fields.Select(f => f.Name).Union(_item.Fields.Select(f => f.Name));
        foreach (var name in names)
        {
            var mine = _item.Fields.FirstOrDefault(f => f.Name == name);
            var theirs = other.Fields.FirstOrDefault(f => f.Name == name);
            if (mine?.Value != theirs?.Value || mine?.Kind != theirs?.Kind) changes.Add(name.ToLowerInvariant());
        }
        if (other.Notes != _item.Notes) changes.Add("notes");
        if (!other.Attachments.Select(a => a.Id).SequenceEqual(_item.Attachments.Select(a => a.Id))) changes.Add("documents");
        return changes.Count == 0 ? "Same content" : "Different " + string.Join(", ", changes.Distinct());
    }

    private void Reload()
    {
        if (Uid is null || _store.Get(Uid) is not { } entry) return;
        _item = entry.Item;
        Load(_item);
    }

    private void Load(VaultItem item)
    {
        Type = VaultCardViewModel.TypeOf(item.Kind);
        Title = item.Title;
        Notes = item.Notes;
        Tags = string.Join(", ", item.Tags);
        Favorite = item.Favorite;
        Icon = item.Icon;
        Fields.Clear();
        foreach (var field in item.Fields) Fields.Add(new FieldViewModel(this, field));
        Attachments.Clear();
        foreach (var attachment in item.Attachments)
        {
            var status = _files.IsUploading(attachment) ? "only on this device, uploading" : _files.IsOnThisDevice(attachment) ? "on this device" : "in the cloud";
            Attachments.Add(new AttachmentViewModel(this, attachment, status));
        }
        History.Clear();
        foreach (var version in item.History) History.Add(new VersionViewModel(this, version));
        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(HasConflicts));
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        Error = null;
        Progress = 0;
        try
        {
            await work().ConfigureAwait(true);
            Touch();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex switch
            {
                VaultLockedException => "The vault locked. Unlock it and try again.",
                BlobUnavailableException or BlobTooLargeException => ex.Message,
                System.Security.Cryptography.CryptographicException => "This document is damaged or was changed; it cannot be opened.",
                HttpRequestException => "The document is not on this device and the server cannot be reached.",
                _ => ex.Message,
            };
        }
        finally
        {
            IsBusy = false;
        }
    }
}
