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
    public async Task Items_are_created_in_the_editor_opened_on_their_card_searched_and_trashed()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Login);
        Assert.True(items.IsEditorOpen); // adding happens in the editor above the list
        var detail = items.Detail!;
        detail.Title = "Bank";
        detail.Fields.Single(f => f.Kind == VaultFieldKind.Username).Value = "anh@example.com";
        var password = detail.Fields.Single(f => f.IsPassword);
        password.GenerateCommand.Execute(null);
        var generated = password.Value;
        detail.SaveCommand.Execute(null);

        Assert.False(items.IsEditorOpen);
        var card = Assert.Single(items.Cards);
        Assert.Equal("Bank", card.Title);
        Assert.Equal("anh@example.com", card.Username);
        Assert.False(card.IsExpanded); // a new item's card is closed once saved
        Assert.Null(items.Detail);
        items.ToggleCardCommand.Execute(card);
        Assert.Equal("••••••••••", items.Detail!.Fields.Single(f => f.IsPassword).Display);
        var secret = items.Detail.Fields.Single(f => f.IsPassword);
        secret.ToggleRevealCommand.Execute(null); // the eye next to Copy
        Assert.Equal(generated, secret.Display);
        items.ToggleCardCommand.Execute(card);

        card.CopyUsernameCommand.Execute(null);
        Assert.Equal("anh@example.com", _platform.CopiedText);
        card.CopyCommand.Execute(null);
        Assert.Equal(generated, _platform.CopiedSecret);

        // Search finds titles and usernames, never secret values.
        items.Search = "example.com";
        Assert.Single(items.Cards);
        items.Search = generated[..6];
        Assert.Empty(items.Cards);
        items.Search = "";

        items.ToggleCardCommand.Execute(items.Cards[0]);
        await items.Detail!.MoveToTrashCommand.ExecuteAsync(null);
        Assert.Empty(items.Cards);
        items.ShowFilterCommand.Execute(VaultFilter.Trash);
        var trashed = Assert.Single(items.Cards);
        Assert.False(trashed.CanCopySecret); // the trash is for restoring, not copying
        Assert.False(trashed.HasUsername);
    }

    [Fact]
    public async Task A_token_card_shows_its_title_and_the_hidden_token_with_copy()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Token);
        var detail = items.Detail!;
        var field = Assert.Single(detail.Fields);
        Assert.Equal("Token", field.Name);
        Assert.True(field.IsSecret);
        Assert.Equal(VaultItemType.Token, detail.Type);
        detail.Title = "OpenAI API";
        field.Value = "sk-live-abc";
        detail.SaveCommand.Execute(null);

        Assert.Equal(VaultTab.Credentials, items.Tab);
        var card = Assert.Single(items.Cards);
        Assert.True(card.IsToken);
        Assert.False(card.IsLogin); // no avatar, no username line
        Assert.True(card.HasSecret);
        Assert.Equal("Copy the token", card.CopyLabel);
        Assert.DoesNotContain("sk-live", card.SecretDisplay);
        card.ToggleRevealCommand.Execute(null);
        Assert.Equal("sk-live-abc", card.SecretDisplay);
        card.CopyCommand.Execute(null);
        Assert.Equal("sk-live-abc", _platform.CopiedSecret);
        Assert.Null(_platform.CopiedText);
    }

    [Fact]
    public async Task An_info_card_shows_its_title_and_description_and_new_info_starts_empty()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("PIN", "1", VaultFieldKind.Secret)] });
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Note);
        Assert.True(items.IsOtherTab);
        var detail = items.Detail!;
        Assert.Equal(VaultItemType.Info, detail.Type);
        Assert.Empty(detail.Fields); // Info has the fields you add
        Assert.Equal("Description", detail.NotesLabel);
        detail.Title = "Wi-Fi";
        detail.Notes = "Router in the hall";
        detail.SaveCommand.Execute(null);

        var wifi = items.Cards.Single(c => c.Title == "Wi-Fi");
        Assert.True(wifi.IsInfo);
        Assert.True(wifi.HasDescription);
        Assert.Equal("Router in the hall", wifi.Description);
        var visa = items.Cards.Single(c => c.Title == "Visa"); // an older card item is Info too
        Assert.True(visa.IsInfo);
        Assert.False(visa.HasDescription);
        Assert.False(visa.HasSecret); // a card's PIN is not shown on the closed card

        // Editing an older card as Info keeps its own kind (older Helm versions still read it).
        items.ToggleCardCommand.Execute(visa);
        items.Detail!.EditCommand.Execute(null);
        items.Detail.Notes = "Main card";
        items.Detail.SaveCommand.Execute(null);
        Assert.Equal(VaultItemKind.Card, store.Get(visa.Uid)!.Item.Kind);
    }

    [Fact]
    public async Task A_login_without_a_password_shows_no_secret_line()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Forum") with { Fields = [new VaultField("Username", "anh", VaultFieldKind.Username)] });
        app.Items.Refresh();

        var card = Assert.Single(app.Items.Cards);
        Assert.False(card.HasSecret);
        Assert.False(card.CanCopySecret);
        Assert.Equal("", card.SecretDisplay);
        Assert.True(card.HasUsername);
    }

    [Fact]
    public async Task Credentials_and_Other_have_their_own_sub_tabs_and_search_counts_both()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Token, "GitHub token") with { Fields = [new VaultField("Token", "ghp_x", VaultFieldKind.Secret)] });
        store.Add(VaultItem.New(VaultItemKind.Note, "Wi-Fi") with { Notes = "the bank's guest network" });
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("PIN", "1", VaultFieldKind.Password)] });
        var items = app.Items;
        items.Refresh();

        Assert.Equal(VaultTab.Credentials, items.Tab);
        Assert.Equal(["Bank", "GitHub token"], items.Cards.Select(c => c.Title).Order());
        Assert.Equal([VaultFilter.Favorites, VaultFilter.All, VaultFilter.Logins, VaultFilter.Tokens, VaultFilter.Trash], items.Filters); // star first, bin last
        items.ShowFilterCommand.Execute(VaultFilter.Tokens);
        Assert.Equal("GitHub token", Assert.Single(items.Cards).Title);

        items.ShowTabCommand.Execute(VaultTab.Other);
        Assert.True(items.IsOtherTab);
        Assert.False(items.IsCredentialsTab);
        Assert.Equal(VaultFilter.All, items.Filter); // a tab starts on All
        Assert.Equal([VaultFilter.Favorites, VaultFilter.All, VaultFilter.Trash], items.Filters);
        Assert.Equal(["Visa", "Wi-Fi"], items.Cards.Select(c => c.Title).Order());

        // One search for every tab: the headers count the matches of both.
        Assert.Equal("Credentials", items.CredentialsHeader);
        items.Search = "bank";
        Assert.Equal("Credentials (1)", items.CredentialsHeader);
        Assert.Equal("Other (1)", items.OtherHeader); // an Info item's description is searched
        Assert.Equal("Wi-Fi", Assert.Single(items.Cards).Title);
        items.ShowTabCommand.Execute(VaultTab.Credentials);
        Assert.Equal("Bank", Assert.Single(items.Cards).Title);
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

        items.ShowFilterCommand.Execute(VaultFilter.Trash);
        Assert.Equal("Old bank", Assert.Single(items.Cards).Title);
        items.Tab = VaultTab.Other;
        items.ShowFilterCommand.Execute(VaultFilter.Trash);
        Assert.Equal("Old note", Assert.Single(items.Cards).Title);
    }

    [Fact]
    public async Task Changing_the_type_moves_a_token_saved_as_a_login_to_tokens_and_keeps_the_old_version()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Stripe key") with { Fields = [new VaultField("Password", "sk_live_1", VaultFieldKind.Password)] });
        var items = app.Items;
        items.Refresh();
        items.ToggleCardCommand.Execute(items.Cards.Single());
        items.Detail!.EditCommand.Execute(null);
        Assert.Equal([VaultItemType.Login, VaultItemType.Token, VaultItemType.Info], items.Detail.Types);
        items.Detail.Type = VaultItemType.Token;
        items.Detail.SaveCommand.Execute(null);

        var saved = store.Get(uid)!.Item;
        Assert.Equal(VaultItemKind.Token, saved.Kind);
        Assert.Equal("sk_live_1", saved.PrimarySecret); // the fields are kept as they were
        Assert.Equal(VaultItemKind.Login, Assert.Single(saved.History).Item.Kind);
        items.ShowFilterCommand.Execute(VaultFilter.Tokens);
        var card = Assert.Single(items.Cards);
        Assert.True(card.IsToken);
        card.CopyCommand.Execute(null);
        Assert.Equal("sk_live_1", _platform.CopiedSecret);

        // To Info: the item moves to Other and opens there.
        if (!items.Cards.Single().IsExpanded) items.ToggleCardCommand.Execute(items.Cards.Single());
        items.Detail!.EditCommand.Execute(null);
        items.Detail.Type = VaultItemType.Info;
        items.Detail.SaveCommand.Execute(null);
        Assert.Equal(VaultItemKind.Note, store.Get(uid)!.Item.Kind);
        Assert.True(items.IsOtherTab);
        Assert.True(Assert.Single(items.Cards).IsExpanded);
    }

    [Fact]
    public async Task Clicking_a_card_opens_it_and_again_closes_it_but_never_an_edit()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Login, "Mail"));
        var items = app.Items;
        items.Refresh();
        var bank = items.Cards.Single(c => c.Title == "Bank");
        var mail = items.Cards.Single(c => c.Title == "Mail");

        items.ToggleCardCommand.Execute(bank);
        Assert.True(bank.IsExpanded);
        Assert.Equal("Bank", items.Detail!.Title);
        items.ToggleCardCommand.Execute(mail); // one open card at a time
        Assert.False(bank.IsExpanded);
        Assert.True(mail.IsExpanded);
        items.ToggleCardCommand.Execute(mail);
        Assert.False(mail.IsExpanded);
        Assert.Null(items.Detail);

        items.ToggleCardCommand.Execute(bank);
        items.Detail!.EditCommand.Execute(null);
        Assert.True(items.IsEditorOpen);
        items.Detail.Title = "Bank (typing)";
        items.ToggleCardCommand.Execute(bank);
        items.CloseDetailCommand.Execute(null);
        Assert.Equal("Bank (typing)", items.Detail!.Title); // an edit stays open
        items.Detail.CancelCommand.Execute(null);
        Assert.False(items.IsEditorOpen);
    }

    [Fact]
    public async Task The_star_on_a_card_toggles_favorite_without_a_new_version()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Bank"));
        var items = app.Items;
        items.Refresh();

        items.ToggleFavoriteCommand.Execute(items.Cards.Single());
        Assert.True(store.Get(uid)!.Item.Favorite);
        Assert.True(items.Cards.Single().Favorite);
        items.ShowFilterCommand.Execute(VaultFilter.Favorites);
        Assert.Single(items.Cards);
        items.ToggleFavoriteCommand.Execute(items.Cards.Single());
        Assert.False(store.Get(uid)!.Item.Favorite);
        Assert.Empty(items.Cards);
        Assert.Empty(store.Get(uid)!.Item.History); // star clicks do not push real edits out of the history
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
        app.Items.ToggleCardCommand.Execute(app.Items.Cards.Single());
        app.Items.Detail!.EditCommand.Execute(null);
        app.Items.Detail.Notes = "new";
        app.Items.Detail.SaveCommand.Execute(null);
        Assert.Equal("new", store.Get(uid)!.Item.Notes);
        Assert.Single(app.Items.Detail!.History);

        session.Lock("test");
        Assert.Empty(app.Items.Cards);
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
