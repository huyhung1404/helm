using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Gallery;

/// <summary>Which screen of the vault a Gallery entry opens on.</summary>
internal enum VaultStage
{
    /// <summary>No vault yet: create one or restore a backup.</summary>
    Setup,
    /// <summary>A vault that is locked (the password is typed in for you).</summary>
    Locked,
    /// <summary>Just created: the Emergency Kit.</summary>
    Kit,
    /// <summary>Unlocked, with items.</summary>
    Items,
    /// <summary>Unlocked, nothing saved yet.</summary>
    Empty,
}

/// <summary>What auto-type's picker (Windows) shows in a Gallery entry.</summary>
internal enum AutoTypeStage
{
    /// <summary>Two logins matched the app in front: they come first, with "offer it in … next time".</summary>
    Likely,
    /// <summary>Something typed into the search.</summary>
    Search,
    /// <summary>The search found nothing.</summary>
    NoMatch,
}

/// <summary>
/// The real Vault view over a real (in-memory-sized) vault in a scratch folder: real keys, session and store, a fake
/// platform. Like tests/Helm.Tests/VaultViewsTests.cs, but with Argon2 settings as shipped.
/// </summary>
internal static class VaultDemo
{
    private const string Password = "correct horse battery staple";

    public static Control Create(FakeModule module, VaultStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var paths = new HelmPaths(dir);
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
        var db = new SyncDatabase(paths.SyncDatabaseFile, key);
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs);
        var records = new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options);
        var keyrings = new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection });
        var settings = new SettingsStoreFactory(paths);
        var deviceUnlock = new NoDeviceUnlock();
        var session = new VaultSession(keyrings, settings, deviceUnlock, engine);
        var store = new VaultStore(records, session);
        var files = new VaultFiles(store, blobs, session);
        var backup = new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings);
        var ui = new AvaloniaUi();
        var platform = new DemoPlatform();
        var app = new VaultAppViewModel(session, store, files, backup, engine, platform, ui);
        var settingsViewModel = new VaultSettingsViewModel(session, store, files, backup, app, deviceUnlock, platform, ui, settings);

        if (stage != VaultStage.Setup) Prepare(session, store, app, stage);
        return new VaultView(module, app, settingsViewModel, new DemoHost());
    }

    private static void Prepare(VaultSession session, VaultStore store, VaultAppViewModel app, VaultStage stage)
    {
        // Off the UI thread: its continuations would otherwise wait for the thread this blocks.
        var recovery = Task.Run(() => session.CreateAsync(Password)).GetAwaiter().GetResult();
        if (stage == VaultStage.Kit)
        {
            app.ShowKit(recovery);
            return;
        }
        if (stage != VaultStage.Empty) Seed(store);
        if (stage != VaultStage.Locked) return;
        session.Lock("demo");
        // The lock screen forgets what was typed when it appears: type it in after.
        Dispatcher.UIThread.Post(() => app.Password = Password, DispatcherPriority.Background);
    }

    private static void Seed(VaultStore store)
    {
        static VaultField F(string name, string value, VaultFieldKind kind) => new(name, value, kind);

        store.Add(VaultItem.New(VaultItemKind.Login, "GitHub") with
        {
            Fields = [F("Username", "huyhung1404", VaultFieldKind.Username), F("Password", "gh-Tz7-vault-demo", VaultFieldKind.Password),
                F("2FA", "JBSWY3DPEHPK3PXP", VaultFieldKind.Totp), F("Website", "https://github.com", VaultFieldKind.Url)],
            Favorite = true,
            Tags = ["work"],
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Gmail") with
        {
            Fields = [F("Username", "hung@example.com", VaultFieldKind.Username), F("Password", "mail-demo-pass-1", VaultFieldKind.Password),
                F("2FA", "KRSXG5CTMVRXEZLU", VaultFieldKind.Totp)],
            Favorite = true,
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Facebook") with
        {
            Fields = [F("Username", "hung.nguyen", VaultFieldKind.Username), F("Password", "fb-demo-pass-2", VaultFieldKind.Password)],
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Zalo") with
        {
            Fields = [F("Phone", "0901 234 567", VaultFieldKind.Username), F("Password", "zalo-demo-pass-3", VaultFieldKind.Password)],
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Cloudflare") with
        {
            Fields = [F("Username", "admin@huyhung1404.com", VaultFieldKind.Username), F("Password", "cf-demo-pass-4", VaultFieldKind.Password),
                F("Website", "https://dash.cloudflare.com", VaultFieldKind.Url)],
            Tags = ["work", "server"],
        });
        store.Add(VaultItem.New(VaultItemKind.Token, "OpenAI API key") with
        {
            Fields = [F("Token", "sk-demo-1234567890abcdef", VaultFieldKind.Secret)],
            Tags = ["work"],
        });
        store.Add(VaultItem.New(VaultItemKind.Card, "Techcombank Visa") with
        {
            Fields = [F("Cardholder", "NGUYEN HUNG", VaultFieldKind.Text), F("Number", "4111 1111 1111 1111", VaultFieldKind.Secret),
                F("Expires", "08/29", VaultFieldKind.Text), F("CVV", "123", VaultFieldKind.Secret)],
            Notes = "Main card. Limit 50 triệu.",
            Tags = ["bank"],
        });
        store.Add(VaultItem.New(VaultItemKind.Identity, "Căn cước công dân") with
        {
            Fields = [F("Name", "Nguyễn Hùng", VaultFieldKind.Text), F("ID number", "079 090 000 123", VaultFieldKind.Secret), F("Date of birth", "1990-01-01", VaultFieldKind.Text)],
            Tags = ["bank"],
        });
        store.Add(VaultItem.New(VaultItemKind.Note, "Wi-Fi ở nhà") with
        {
            Notes = "Mạng: HuyHung_5G\nMật khẩu: demo-wifi-2026\nRouter đặt ở hành lang, đèn xanh là ổn.",
        });
        var trashed = store.Add(VaultItem.New(VaultItemKind.Login, "Old forum account") with
        {
            Fields = [F("Username", "hungnh", VaultFieldKind.Username), F("Password", "forum-demo-pass-5", VaultFieldKind.Password)],
        });
        store.MoveToTrash(trashed);
    }

    /// <summary>Auto-type's picker over pretend logins, as it floats over the app to type into.</summary>
    public static Control AutoType(AutoTypeStage stage) => new Panel
    {
        Children =
        {
            new Helm.Core.Ui.Backdrop(),
            new AutoTypePickerView(AutoTypePicker(stage)) { MaxWidth = 460 + 32, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Avalonia.Thickness(0, 24, 0, 0) },
        },
    };

    /// <summary>The picker's view model as auto-type loads it: the likely logins, all of them, the "remember" line (the UI tests drive it).</summary>
    public static AutoTypePickerViewModel AutoTypePicker(AutoTypeStage stage)
    {
        var logins = AutoTypeLogins();
        var vm = new AutoTypePickerViewModel();
        vm.Load([logins[1], logins[2]], logins, stage == AutoTypeStage.Likely ? "Offer it in Unity Hub next time" : null);
        vm.Search = stage switch
        {
            AutoTypeStage.Search => "git",
            AutoTypeStage.NoMatch => "bank of mars",
            _ => "",
        };
        return vm;
    }

    /// <summary>Pretend logins (never stored: auto-type only reads them).</summary>
    public static IReadOnlyList<VaultEntry> AutoTypeLogins()
    {
        (string Title, string User)[] logins =
        [
            ("Cloudflare", "admin@example.com"),
            ("Unity ID", "hung.dev@example.com"),
            ("Unity Hub (work)", "hung@studio.example"),
            ("GitHub", "hung-dev"),
            ("GitLab (studio)", "hung"),
            ("Google", "hung.dev@example.com"),
            ("Jira", "hung@studio.example"),
            ("Zalo", "0901 234 567"),
        ];
        return logins.Select((l, i) => new VaultEntry($"demo-{i}", VaultItem.New(VaultItemKind.Login, l.Title) with
        {
            Fields = [new VaultField("Username", l.User, VaultFieldKind.Username), new VaultField("Password", "demo-pass", VaultFieldKind.Password)],
        }, false, null, DateTimeOffset.UnixEpoch, [], $"record-{i}")).ToList();
    }

    private sealed class DemoHost : IVaultPageHost
    {
        public Control? CreatePlatformSettings() => null;

        public bool CanLockWithSystem => true;
    }

    /// <summary>What the platform does for the vault: nothing here, except real confirmations.</summary>
    private sealed class DemoPlatform : IVaultPlatform
    {
        public Task<VaultPickedFile?> PickFileAsync(CancellationToken ct) => Task.FromResult<VaultPickedFile?>(null);

        public Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct) => Task.FromResult<Stream?>(null);

        public Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct) => Task.CompletedTask;

        public void CopySecret(string text) { }

        public void CopyText(string text) { }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => VaultDialogs.ConfirmAsync(title, message, confirmText);

        public Task<string?> PickBackupLocationAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct) => Task.CompletedTask;

        public void ShowVault() { }
    }
}
