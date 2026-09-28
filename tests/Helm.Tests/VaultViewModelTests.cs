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
    public async Task A_token_has_one_hidden_field_and_its_row_shows_and_copies_the_token()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Token);
        var detail = items.Detail!;
        var field = Assert.Single(detail.Fields);
        Assert.Equal("Token", field.Name);
        Assert.True(field.IsSecret);
        detail.Title = "OpenAI API";
        field.Value = "sk-live-abc";
        detail.SaveCommand.Execute(null);

        Assert.Equal(VaultTab.LoginsAndTokens, items.Tab);
        var row = Assert.Single(items.PasswordRows);
        Assert.True(row.IsToken);
        Assert.True(row.HasSecret);
        Assert.Equal("Token", row.Subtitle);
        Assert.Equal("Copy the token", row.CopyLabel);
        Assert.DoesNotContain("sk-live", row.SecretDisplay);
        row.ToggleRevealCommand.Execute(null);
        Assert.Equal("sk-live-abc", row.SecretDisplay);
        row.CopyCommand.Execute(null);
        Assert.Equal("sk-live-abc", _platform.CopiedSecret);
        Assert.Null(_platform.CopiedText);
    }

    [Fact]
    public async Task A_login_without_a_password_shows_no_secret_line()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Forum") with { Fields = [new VaultField("Username", "anh", VaultFieldKind.Username)] });
        app.Items.Refresh();

        var row = Assert.Single(app.Items.PasswordRows);
        Assert.False(row.HasSecret);
        Assert.Equal("", row.SecretDisplay);
        Assert.Equal("Copy the password", row.CopyLabel);
    }

    [Fact]
    public async Task Logins_and_tokens_have_their_own_tab_and_everything_else_is_in_Other()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Token, "GitHub token") with { Fields = [new VaultField("Token", "ghp_x", VaultFieldKind.Secret)] });
        store.Add(VaultItem.New(VaultItemKind.Note, "Wi-Fi"));
        // A card with a PIN is still a card: it stays in Other.
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("PIN", "1", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Document, "Passport") with { Icon = [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0, 0] });
        var items = app.Items;
        items.Refresh();

        Assert.Equal(VaultTab.LoginsAndTokens, items.Tab);
        Assert.True(items.IsPasswordsView);
        Assert.Equal(["Bank", "GitHub token"], items.PasswordRows.Select(r => r.Title).Order());
        Assert.Equal([VaultFilter.All, VaultFilter.Logins, VaultFilter.Tokens, VaultFilter.Favorites, VaultFilter.Trash], items.Filters);
        items.Filter = VaultFilter.Tokens;
        Assert.Equal("GitHub token", Assert.Single(items.PasswordRows).Title);

        items.ShowTabCommand.Execute(VaultTab.Other);
        Assert.True(items.IsOtherTab);
        Assert.False(items.IsLoginsTab);
        Assert.Equal(VaultFilter.All, items.Filter); // a tab starts on All
        Assert.True(items.IsListView);
        Assert.Empty(items.PasswordRows);
        Assert.Equal(["Passport", "Visa", "Wi-Fi"], items.Rows.Select(r => r.Title).Order());
        Assert.DoesNotContain(VaultFilter.Tokens, items.Filters);
        Assert.True(items.Rows.Single(r => r.Title == "Passport").HasIcon);
        Assert.False(items.Rows.Single(r => r.Title == "Visa").HasIcon);

        // New items open in the tab they belong to.
        items.NewCommand.Execute(VaultItemKind.Token);
        Assert.True(items.IsLoginsTab);
    }

    [Fact]
    public async Task Each_tab_has_its_own_trash()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var login = store.Add(VaultItem.New(VaultItemKind.Login, "Old bank"));
        var note = store.Add(VaultItem.New(VaultItemKind.Note, "Old note"));
        store.MoveToTrash(login);
        store.MoveToTrash(note);
        var items = app.Items;

        items.Filter = VaultFilter.Trash;
        Assert.True(items.IsListView); // the trash is a plain list, with no copy buttons
        Assert.Equal("Old bank", Assert.Single(items.Rows).Title);
        items.Tab = VaultTab.Other;
        items.Filter = VaultFilter.Trash;
        Assert.Equal("Old note", Assert.Single(items.Rows).Title);
    }

    [Fact]
    public async Task Changing_the_type_moves_a_token_saved_as_a_login_to_tokens_and_keeps_the_old_version()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Stripe key") with { Fields = [new VaultField("Password", "sk_live_1", VaultFieldKind.Password)] });
        var items = app.Items;
        items.Refresh();
        items.SelectedPassword = items.PasswordRows.Single();
        items.Detail!.EditCommand.Execute(null);
        Assert.Contains(VaultItemKind.Token, items.Detail.Kinds);
        items.Detail.Kind = VaultItemKind.Token;
        items.Detail.SaveCommand.Execute(null);

        var saved = store.Get(uid)!.Item;
        Assert.Equal(VaultItemKind.Token, saved.Kind);
        Assert.Equal("sk_live_1", saved.PrimarySecret); // the fields are kept as they were
        Assert.Equal(VaultItemKind.Login, Assert.Single(saved.History).Item.Kind);
        items.Filter = VaultFilter.Tokens;
        var row = Assert.Single(items.PasswordRows);
        Assert.True(row.IsToken);
        row.CopyCommand.Execute(null);
        Assert.Equal("sk_live_1", _platform.CopiedSecret);
    }

    [Fact]
    public async Task Clicking_the_open_row_again_closes_it_but_never_an_edit()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)] });
        var items = app.Items;
        items.Refresh();

        items.SelectedPassword = items.PasswordRows.Single();
        Assert.NotNull(items.Detail);
        items.CloseDetailCommand.Execute(null);
        Assert.Null(items.Detail);
        Assert.Null(items.Selected);
        Assert.Null(items.SelectedPassword);
        items.SelectedPassword = items.PasswordRows.Single(); // and open again
        Assert.Equal("Bank", items.Detail!.Title);

        items.Detail.EditCommand.Execute(null);
        items.Detail.Title = "Bank (typing)";
        items.CloseDetailCommand.Execute(null);
        Assert.Equal("Bank (typing)", items.Detail!.Title); // an edit stays open
    }

    [Fact]
    public void An_item_kind_from_a_newer_Helm_is_refused_not_guessed()
    {
        // What an older Helm does with a Token (or any kind added later): the item fails to parse, so the store lists
        // it as "cannot be opened" and keeps it, instead of reading it as some other kind and saving that back.
        var future = System.Text.Encoding.UTF8.GetBytes("""{"kind":"somethingNewer","title":"x","fields":[]}""");
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<VaultItem>(future, Helm.Core.Settings.HelmJson.Options));
        var token = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(VaultItem.New(VaultItemKind.Token, "t"), Helm.Core.Settings.HelmJson.Options);
        using var json = System.Text.Json.JsonDocument.Parse(token);
        Assert.Equal("token", json.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Editing_keeps_the_old_version_and_locking_drops_everything_decrypted()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Note, "Wifi") with { Notes = "old" });
        app.Items.Tab = VaultTab.Other;
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
