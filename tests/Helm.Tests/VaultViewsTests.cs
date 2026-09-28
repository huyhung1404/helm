using System.Windows;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;
using Helm.Modules.Vault.Views;
using Wpf.Ui.Markup;

namespace Helm.Tests;

/// <summary>
/// Builds the vault's WPF views with the app's resources. A wrong resource key, icon or binding type in XAML compiles
/// fine and only fails when the view is created, so this catches it before anyone opens the vault.
/// </summary>
public sealed class VaultViewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_vault_window_and_item_view_load_on_every_screen() => RunSta(() =>
    {
        EnsureApplication();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var paths = new HelmPaths(_dir);
        using var db = new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local);
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        using var engine = new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs);
        using var records = new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options);
        using var keyrings = new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection });
        using var settings = new SettingsStoreFactory(paths);
        using var session = new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine) { NewKdf = VaultCryptoTests.CheapKdf };
        using var store = new VaultStore(records, session);
        var files = new VaultFiles(store, blobs, session);
        var backup = new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings);
        var app = new VaultAppViewModel(session, store, files, backup, engine, new NullPlatform(), new Dispatcher());

        var window = new VaultView { DataContext = app };
        Layout(window);
        Assert.True(app.IsSetup);

        // The guide: every block builds, and a link scrolls to its section.
        var guide = new VaultGuideView();
        Layout(guide);
        Assert.True(guide.ScrollTo("sao-lưu"));
        Assert.False(guide.ScrollTo("no-such-section"));
        Layout(guide);
        app.ShowGuideCommand.Execute(null);
        Assert.True(app.IsGuideOpen);
        app.CloseGuideCommand.Execute(null);

        app.NewPassword = app.ConfirmPassword = "correct horse battery staple";
        Pump(app.CreateCommand.ExecuteAsync(null));
        Assert.True(app.IsItems);
        Assert.True(app.HasKit);
        Layout(window);
        app.KitConfirmation = app.Kit!.RecoveryKey;
        app.ConfirmKitCommand.Execute(null);
        Assert.False(app.HasKit);

        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with
        {
            Fields = [new VaultField("Username", "anh@example.com", VaultFieldKind.Username), new VaultField("Password", "hunter2", VaultFieldKind.Password),
                new VaultField("Website", "https://bank.example", VaultFieldKind.Url)],
            Favorite = true,
        });
        store.Add(VaultItem.New(VaultItemKind.Note, "Wi-Fi at home") with { Notes = "Router in the hall" });
        var uid = store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Notes = "Main card", Tags = ["bank", "travel"] });
        Pump(Task.CompletedTask);
        app.Items.Refresh();
        app.Items.Selected = app.Items.Rows.Single(r => r.Uid == uid);
        Layout(window);
        app.Items.Detail!.EditCommand.Execute(null);
        Layout(window);

        var detail = new ItemDetailView { DataContext = app.Items.Detail };
        Layout(detail);

        // The Passwords view: rows with a picture icon and with letter avatars.
        app.Items.Detail!.CancelCommand.Execute(null);
        store.Add(VaultItem.New(VaultItemKind.Login, "GitHub") with
        {
            Fields = [new VaultField("Username", "huyhung1404", VaultFieldKind.Username), new VaultField("Password", "gh-secret", VaultFieldKind.Password)],
            Icon = PngOf(Helm.Modules.Vault.VaultIcon.Image),
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Zalo") with { Fields = [new VaultField("Phone", "0901 234 567", VaultFieldKind.Username), new VaultField("Password", "z", VaultFieldKind.Password)] });
        app.Items.Filter = VaultFilter.Passwords;
        Assert.Equal(3, app.Items.PasswordRows.Count);
        app.Items.PasswordRows.Single(r => r.Title == "Bank").ToggleRevealCommand.Execute(null);
        Layout(window);
        app.Items.SelectedPassword = app.Items.PasswordRows.Single(r => r.Title == "GitHub");
        Assert.Equal("GitHub", app.Items.Detail!.Title);
        Layout(window);

        // The icon, large and at list size.
        foreach (var size in new[] { 256.0, 32.0 })
        {
            var icon = new System.Windows.Controls.Image { Source = Helm.Modules.Vault.VaultIcon.Image, Width = size, Height = size };
            Layout(icon);
        }

        session.Lock("test");
        Pump(Task.CompletedTask);
        Assert.True(app.IsUnlock);
        Layout(window);
    });

    private static byte[] PngOf(System.Windows.Media.ImageSource image)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawImage(image, new Rect(0, 0, 128, 128));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(128, 128, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }

    /// <summary>Runs the dispatcher until the task completes, like the app's message loop would.</summary>
    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
        // Let posted work (screen changes, list refreshes) run too.
        var idle = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => idle.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(idle);
    }

    private static int s_shot;

    private static void Layout(FrameworkElement element)
    {
        // A window that is never shown does not lay out; its content does.
        var target = element is Window { Content: FrameworkElement content } ? content : element;
        target.Measure(new Size(1040, 700));
        target.Arrange(new Rect(0, 0, 1040, 700));
        target.UpdateLayout();
        Snapshot(element);
    }

    /// <summary>
    /// With HELM_VAULT_SNAPSHOTS set to a folder, every screen is rendered to a PNG there (in-process, off-screen: no
    /// window is shown and nothing on the desktop is captured), for checking the layout by eye.
    /// </summary>
    private static void Snapshot(FrameworkElement element)
    {
        if (Environment.GetEnvironmentVariable("HELM_VAULT_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var visual = element is Window { Content: FrameworkElement content } ? content : element;
        var size = new Size(1040, 700);
        var host = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Width = size.Width,
            Height = size.Height,
        };
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        host.Measure(size);
        host.Arrange(new Rect(size));
        bitmap.Render(host);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, $"{++s_shot:00}-{element.GetType().Name}.png"));
        encoder.Save(file);
    }

    /// <summary>The resources App.xaml merges: WPF-UI's theme and controls, then Helm's styles.</summary>
    private static void EnsureApplication()
    {
        if (Application.Current is null) _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = Application.Current!.Resources.MergedDictionaries;
        if (resources.Count > 0) return;
        resources.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
        resources.Add(new ControlsDictionary());
        resources.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Helm.Core.Windows;component/Ui/HelmStyles.xaml") });
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    /// <summary>Like the app's UiDispatcher: work is posted to the WPF dispatcher of the test thread.</summary>
    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        public bool CheckAccess() => _dispatcher.CheckAccess();
        public void Post(Action action) => _dispatcher.BeginInvoke(action);
        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
    }

    private sealed class NullPlatform : IVaultPlatform
    {
        public Task<VaultPickedFile?> PickFileAsync(CancellationToken ct) => Task.FromResult<VaultPickedFile?>(null);
        public Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct) => Task.FromResult<Stream?>(null);
        public Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct) => Task.CompletedTask;
        public void CopySecret(string text) { }
        public void CopyText(string text) { }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
        public Task<string?> PickBackupLocationAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct) => Task.CompletedTask;
        public void ShowVault() { }
    }
}
