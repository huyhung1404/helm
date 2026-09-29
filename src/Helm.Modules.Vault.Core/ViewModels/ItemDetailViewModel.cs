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
    [ObservableProperty] private string? _attachmentId = field.AttachmentId;

    /// <summary>An image field's picture, decrypted small for the view (null until loaded, or for other fields).</summary>
    [ObservableProperty] private byte[]? _preview;

    public bool IsSecret => Kind is VaultFieldKind.Secret or VaultFieldKind.Password;

    /// <summary>Image, document, text file or keystore: the value is a file of the item.</summary>
    public bool IsFile => VaultField.IsFileKind(Kind);

    public bool IsValueField => !IsFile;

    public bool IsImage => Kind == VaultFieldKind.Image;

    public bool HasFile => AttachmentId is not null && Owner.FindAttachment(AttachmentId) is not null;

    public bool HasPreview => Preview is { Length: > 0 };

    /// <summary>"passport.pdf · 1.2 MB" (or "No file yet").</summary>
    public string FileText => AttachmentId is { } id && Owner.FindAttachment(id) is { } a
        ? Sizes.Join(a.Name, Sizes.Format(a.Size))
        : "No file yet";

    /// <summary>Copy only for text values (a file is opened or saved instead).</summary>
    public bool CanCopy => HasValue && !IsFile;

    public bool IsPassword => Kind == VaultFieldKind.Password;

    public bool IsMultiline => Kind == VaultFieldKind.Multiline;

    public bool IsLink => Kind == VaultFieldKind.Url && Uri.TryCreate(Value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    /// <summary>What the view shows when not editing: dots for a hidden secret.</summary>
    public string Display => Value.Length == 0 ? "—" : IsSecret && !IsRevealed ? "••••••••••" : Value;

    public bool HasValue => Value.Length > 0;

    public ItemDetailViewModel Owner { get; } = owner;

    public VaultField ToField() => new(Name.Trim(), Value, Kind, IsFile ? AttachmentId : null);

    partial void OnAttachmentIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FileText));
    }

    partial void OnPreviewChanged(byte[]? value) => OnPropertyChanged(nameof(HasPreview));

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(HasValue));
    }

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(Display));

    partial void OnKindChanged(VaultFieldKind value)
    {
        OnPropertyChanged(nameof(IsFile));
        OnPropertyChanged(nameof(IsValueField));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(CanCopy));
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

    /// <summary>Picks the file (encrypted at once; it joins the item on Save).</summary>
    [RelayCommand]
    private Task ChooseFileAsync() => Owner.ChooseFileAsync(this);

    [RelayCommand]
    private Task OpenFileAsync() => Owner.OpenFileAsync(this);

    [RelayCommand]
    private Task SaveFileAsync() => Owner.SaveFileAsync(this);
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
    private readonly Dictionary<string, VaultAttachment> _pending = new(StringComparer.Ordinal);
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
                Fields = Fields.Select(f => f.ToField())
                    .Where(f => f.IsFile ? f.AttachmentId is not null && FindAttachment(f.AttachmentId) is not null : f.Name.Length > 0 || f.Value.Length > 0)
                    .ToList(),
                // Exactly the files the fields hold: new ones join, removed ones leave (they stay in the history).
                Attachments = Fields.Where(f => f.IsFile && f.AttachmentId is not null)
                    .Select(f => FindAttachment(f.AttachmentId!)).OfType<VaultAttachment>()
                    .DistinctBy(a => a.Id).ToList(),
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
            _ when VaultField.IsFileKind(kind) => FileFieldName(kind),
            _ => "Field",
        }, "", kind)));
        Touch();
    }

    /// <summary>The attachment a file field names: one picked in this edit, or one the item already has.</summary>
    internal VaultAttachment? FindAttachment(string id) =>
        _pending.TryGetValue(id, out var pending) ? pending : _item.Attachments.FirstOrDefault(a => a.Id == id);

    private static VaultFieldKind FileKindOf(string name, string mediaType)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic")
            return VaultFieldKind.Image;
        if (ext is ".txt" || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase)) return VaultFieldKind.TextFile;
        if (ext is ".jks" or ".keystore" or ".p12" or ".pfx" or ".bks") return VaultFieldKind.Keystore;
        return VaultFieldKind.Document;
    }

    private static string FileFieldName(VaultFieldKind kind) => kind switch
    {
        VaultFieldKind.Image => "Image",
        VaultFieldKind.TextFile => "Text file",
        VaultFieldKind.Keystore => "Keystore",
        _ => "Document",
    };

    internal async Task ChooseFileAsync(FieldViewModel field)
    {
        var picked = await _platform.PickFileAsync(CancellationToken.None).ConfigureAwait(true);
        if (picked is null) return;
        await using (picked.Content)
        {
            var kind = FileKindOf(picked.Name, picked.MediaType);
            if (field.Kind == VaultFieldKind.Image && kind != VaultFieldKind.Image)
            {
                Error = $"{picked.Name} is not a picture. Choose a PNG, JPEG, GIF, WebP or BMP file, or make the field a Document.";
                return;
            }
            if (field.Kind == VaultFieldKind.TextFile && kind != VaultFieldKind.TextFile)
            {
                Error = $"{picked.Name} is not a .txt file. Choose a text file, or make the field a Document.";
                return;
            }
            await RunAsync(async () =>
            {
                var attachment = await _files.ImportAsync(picked.Name, picked.MediaType, picked.Content, new Progress<double>(p => Progress = p)).ConfigureAwait(true);
                _pending[attachment.Id] = attachment;
                field.AttachmentId = attachment.Id;
                field.Value = picked.Name;
                field.Preview = null;
                if (field.IsImage) field.Preview = await ReadPreviewAsync(attachment).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        Touch();
    }

    internal Task OpenFileAsync(FieldViewModel field) => field.AttachmentId is { } id && FindAttachment(id) is { } attachment
        ? RunAsync(async () =>
        {
            var data = await _files.ReadAllAsync(attachment).ConfigureAwait(true);
            await _platform.OpenFileAsync(attachment.Name, attachment.MediaType, data, CancellationToken.None).ConfigureAwait(true);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
        })
        : Task.CompletedTask;

    internal Task SaveFileAsync(FieldViewModel field) => field.AttachmentId is { } id && FindAttachment(id) is { } attachment
        ? RunAsync(async () =>
        {
            var stream = await _platform.CreateFileAsync(attachment.Name, attachment.MediaType, CancellationToken.None).ConfigureAwait(true);
            if (stream is null) return;
            await using (stream) await _files.OpenAsync(attachment, stream, new Progress<double>(p => Progress = p)).ConfigureAwait(true);
        })
        : Task.CompletedTask;

    /// <summary>Pictures of image fields, decrypted for the view (only small ones: a preview, not a download).</summary>
    private async Task LoadPreviewsAsync()
    {
        foreach (var field in Fields.Where(f => f.IsImage && f.AttachmentId is not null).ToList())
        {
            if (FindAttachment(field.AttachmentId!) is not { } attachment) continue;
            try
            {
                field.Preview = await ReadPreviewAsync(attachment).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                field.Preview = null; // offline, or locked meanwhile: the name and Open still work
            }
        }
    }

    private const long MaxPreviewBytes = 8 * 1024 * 1024;

    private async Task<byte[]?> ReadPreviewAsync(VaultAttachment attachment) =>
        attachment.Size <= MaxPreviewBytes && _session.State == VaultState.Unlocked
            ? await _files.ReadAllAsync(attachment).ConfigureAwait(true)
            : null;

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
        // Documents attached before files were fields show as file fields, so nothing is hidden (they become
        // real fields with the next Save).
        var linked = item.Fields.Where(f => f.IsFile && f.AttachmentId is not null).Select(f => f.AttachmentId!).ToHashSet(StringComparer.Ordinal);
        foreach (var attachment in item.Attachments.Where(a => !linked.Contains(a.Id)))
        {
            var kind = FileKindOf(attachment.Name, attachment.MediaType);
            Fields.Add(new FieldViewModel(this, new VaultField(FileFieldName(kind), attachment.Name, kind, attachment.Id)));
        }
        _pending.Clear();
        _ = LoadPreviewsAsync();
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
