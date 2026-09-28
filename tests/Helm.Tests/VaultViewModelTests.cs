using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Tests;

/// <summary>The vault screens shared by the PC and Android apps, without any UI.</summary>
public sealed class VaultViewModelTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly FakePlatform _platform = new();

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Setup_creates_the_vault_and_holds_the_emergency_kit_until_confirmed()
    {
        var (app, session, _) = NewApp();
        Assert.Equal(VaultScreen.Setup, app.Screen);

        app.NewPassword = Password;
        app.ConfirmPassword = Password + "x";
        await app.CreateCommand.ExecuteAsync(null);
        Assert.Equal("The two passwords differ.", app.Error);

        app.ConfirmPassword = Password;
        await app.CreateCommand.ExecuteAsync(null);
        Assert.Null(app.Error);
        Assert.Equal(VaultScreen.Items, app.Screen);
        Assert.Equal("", app.NewPassword);
        var kit = Assert.IsType<EmergencyKit>(app.Kit);
        Assert.Contains(kit.RecoveryKey, kit.Text);

        await app.SaveKitCommand.ExecuteAsync(null);
        Assert.Same(kit, _platform.SavedKit);
        app.KitConfirmation = "HELMV-WRONG";
        app.ConfirmKitCommand.Execute(null);
        Assert.NotNull(app.KitError);
        app.KitConfirmation = kit.RecoveryKey;
        app.ConfirmKitCommand.Execute(null);
        Assert.Null(app.Kit);
        Assert.True(session.RecoveryKitConfirmed);
    }

    [Fact]
    public async Task Unlock_shows_errors_and_clears_the_typed_password()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        session.Lock("test");
        Assert.Equal(VaultScreen.Unlock, app.Screen);

        app.Password = "wrong wrong wrong wrong";
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Equal("The vault password is not correct.", app.Error);
        Assert.Equal(VaultScreen.Unlock, app.Screen);

        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Equal(VaultScreen.Items, app.Screen);
        Assert.Equal("", app.Password);
    }

    [Fact]
    public async Task Items_are_created_edited_searched_and_trashed_through_the_screens()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Login);
        var detail = items.Detail!;
        Assert.True(detail.IsEditing);
        detail.Title = "Bank";
        detail.Fields.Single(f => f.Kind == VaultFieldKind.Username).Value = "anh@example.com";
        var password = detail.Fields.Single(f => f.IsPassword);
        password.GenerateCommand.Execute(null);
        var generated = password.Value;
        detail.SaveCommand.Execute(null);

        var row = Assert.Single(items.Rows);
        Assert.Equal("Bank", row.Title);
        Assert.Equal("anh@example.com", row.Subtitle);
        Assert.False(items.Detail!.IsEditing);
        Assert.Equal("••••••••••", items.Detail.Fields.Single(f => f.IsPassword).Display);

        items.Detail.Fields.Single(f => f.IsPassword).CopyCommand.Execute(null);
        Assert.Equal(generated, _platform.CopiedSecret);
        Assert.Null(_platform.CopiedText);

        // Search finds titles and usernames, never secret values.
        items.Search = "example.com";
        Assert.Single(items.Rows);
        items.Search = generated[..6];
        Assert.Empty(items.Rows);
        items.Search = "";

        items.Selected = items.Rows[0];
        await items.Detail!.MoveToTrashCommand.ExecuteAsync(null);
        Assert.Empty(items.Rows);
        items.Filter = VaultFilter.Trash;
        Assert.Single(items.Rows);
    }

    [Fact]
    public async Task Editing_keeps_the_old_version_and_locking_drops_everything_decrypted()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Note, "Wifi") with { Notes = "old" });
        app.Items.Refresh();
        app.Items.Selected = app.Items.Rows.Single();
        app.Items.Detail!.EditCommand.Execute(null);
        app.Items.Detail.Notes = "new";
        app.Items.Detail.SaveCommand.Execute(null);
        Assert.Equal("new", store.Get(uid)!.Item.Notes);
        Assert.Single(app.Items.Detail!.History);

        session.Lock("test");
        Assert.Empty(app.Items.Rows);
        Assert.Null(app.Items.Detail);
        Assert.Equal(VaultScreen.Unlock, app.Screen);
    }

    [Fact]
    public async Task Settings_change_the_password_and_export_to_keepass()
    {
        var (app, session, store) = NewApp(out var settings);
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Note, "Note"));

        settings.CurrentPassword = Password;
        settings.NewPassword = "a brand new vault password 7";
        settings.ConfirmPassword = "a brand new vault password 7";
        await settings.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Null(settings.Error);
        Assert.Equal("", settings.CurrentPassword);

        settings.ExportPassword = settings.ExportConfirm = "a keepass export password";
        await settings.ExportKdbxCommand.ExecuteAsync(null);
        Assert.Null(settings.Error);
        Assert.True(_platform.Created.Length > 100);

        session.Lock("test");
        await settings.BackUpNowCommand.ExecuteAsync(null);
        Assert.Equal("Unlock the vault first (Open vault).", settings.Error);
    }

    private (VaultAppViewModel App, VaultSession Session, VaultStore Store) NewApp() => NewApp(out _);

    private (VaultAppViewModel App, VaultSession Session, VaultStore Store) NewApp(out VaultSettingsViewModel settingsViewModel)
    {
        var paths = new HelmPaths(Path.Combine(_dir, "pc"));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths));
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine) { NewKdf = VaultCryptoTests.CheapKdf });
        var store = Own(new VaultStore(records, session));
        var files = new VaultFiles(store, blobs, session);
        var backup = new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings);
        var app = new VaultAppViewModel(session, store, files, backup, engine, _platform, new ImmediateDispatcher());
        settingsViewModel = new VaultSettingsViewModel(session, store, files, backup, app, new NoDeviceUnlock(), _platform, new ImmediateDispatcher(), settings);
        return (app, session, store);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : IVaultPlatform
    {
        public string? CopiedSecret { get; private set; }
        public string? CopiedText { get; private set; }
        public EmergencyKit? SavedKit { get; private set; }
        public byte[] Created { get; private set; } = [];

        public Task<VaultPickedFile?> PickFileAsync(CancellationToken ct) => Task.FromResult<VaultPickedFile?>(null);

        public Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct) =>
            Task.FromResult<Stream?>(new CapturingStream(bytes => Created = bytes));

        public Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct) => Task.CompletedTask;
        public void CopySecret(string text) => CopiedSecret = text;
        public void CopyText(string text) => CopiedText = text;
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
        public Task<string?> PickBackupLocationAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct)
        {
            SavedKit = kit;
            return Task.CompletedTask;
        }

        public void ShowVault() { }

        private sealed class CapturingStream(Action<byte[]> done) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                done(ToArray());
                base.Dispose(disposing);
            }
        }
    }
}
