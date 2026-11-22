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
        // Files are fields: a picture (with its preview) and a keystore.
        var scan = files.ImportAsync("scan.png", "image/png", new MemoryStream(PngOf(Helm.Modules.Vault.VaultIcon.Image)));
        var keystore = files.ImportAsync("release.keystore", "application/octet-stream", new MemoryStream([1, 2, 3]));
        Pump(Task.WhenAll(scan, keystore));
        store.Save(uid, store.Get(uid)!.Item with
        {
            Fields = [new VaultField("Scan", "scan.png", VaultFieldKind.Image, scan.Result.Id), new VaultField("Keystore", "release.keystore", VaultFieldKind.Keystore, keystore.Result.Id)],
            Attachments = [scan.Result, keystore.Result],
        });
        Pump(Task.CompletedTask);
        // The PC shows an item at once (the first one), and the list selection opens the one clicked.
        Assert.True(app.Items.AutoSelectFirst);
        Assert.NotNull(app.Items.SelectedCard);
        var list = (System.Windows.Controls.ListBox)window.FindName("ItemList");
        list.SelectedItem = app.Items.Cards.Single(c => c.Uid == uid);
        Assert.Equal("Visa", app.Items.Detail!.Title);
        Layout(window, 1280);
        app.Items.Detail!.EditCommand.Execute(null);
        Assert.True(app.Items.IsEditorOpen);
        Layout(window, 1280);

        Pump(Task.Delay(300)); // the picture's preview is decrypted in the background
        Assert.True(app.Items.Editor!.Fields.Single(f => f.IsImage).HasPreview);
        var editor = new ItemEditorView { DataContext = app.Items.Editor };
        Layout(editor);
        Pump(app.Items.Editor!.CancelCommand.ExecuteAsync(null)); // nothing typed: closes without asking
        Assert.False(app.Items.IsEditorOpen);
        var detail = new ItemDetailView { DataContext = app.Items.Detail };
        Layout(detail);

        // A login with a picture and a two-factor code, a letter avatar, and a token.
        store.Add(VaultItem.New(VaultItemKind.Login, "GitHub") with
        {
            Fields = [new VaultField("Username", "huyhung1404", VaultFieldKind.Username), new VaultField("Password", "gh-secret", VaultFieldKind.Password),
                new VaultField("2FA", "JBSWY3DPEHPK3PXP", VaultFieldKind.Totp)],
            Icon = PngOf(Helm.Modules.Vault.VaultIcon.Image),
            Tags = ["work"],
        });
        store.Add(VaultItem.New(VaultItemKind.Login, "Zalo") with { Fields = [new VaultField("Phone", "0901 234 567", VaultFieldKind.Username), new VaultField("Password", "z", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Token, "OpenAI API") with { Fields = [new VaultField("Token", "sk-test-123", VaultFieldKind.Secret)], Tags = ["work"] });
        Pump(Task.CompletedTask);
        Assert.Equal(6, app.Items.Cards.Count);
        Assert.True(app.Items.Cards.Single(c => c.Title == "OpenAI API").IsToken);
        Assert.Equal(["bank", "travel", "work"], app.Items.TagSections.Select(s => s.Label));
        Layout(window, 1280);

        // A section in the left column: 2FA & tokens lists the token and the login with a code.
        var tokens = Descendants<System.Windows.Controls.Button>(window)
            .Single(b => b.CommandParameter is VaultSection { Kind: VaultSectionKind.Tokens } && b.Style == window.TryFindResource("NavButton"));
        tokens.Command.Execute(tokens.CommandParameter);
        Assert.Equal(["GitHub", "OpenAI API"], app.Items.Cards.Select(c => c.Title));
        var gitHub = app.Items.Cards.Single(c => c.Title == "GitHub");
        list.SelectedItem = gitHub;
        Assert.Equal("GitHub", app.Items.Detail!.Title);
        // Two-factor: the row and the item show the current code, never the key.
        Assert.True(gitHub.HasTotp);
        Assert.Matches(@"^\d{3} \d{3}$", gitHub.TotpCode);
        Assert.InRange(gitHub.TotpProgress, 1, 100);
        var code = app.Items.Detail!.Fields.Single(f => f.IsTotp);
        Assert.Equal(code.TotpCode, code.Display);
        Assert.DoesNotContain("JBSW", code.Display);
        Layout(window, 1280);
        Layout(new ItemDetailView { DataContext = app.Items.Detail });

        // The sections shrink to icons, then become chips above the list, as the window narrows.
        Layout(window, 900);
        Assert.Equal(VaultLayout.Compact, window.Layout);
        Assert.True(window.IsCompactNav);
        Layout(window, 600);
        Assert.Equal(VaultLayout.Narrow, window.Layout);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ChipRow")).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("DetailPane")).Visibility); // the open item takes the width
        app.Items.CloseDetailCommand.Execute(null); // Back
        Layout(window, 600);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ListPane")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("DetailPane")).Visibility);
        Layout(window, 1280);
        Assert.Equal(VaultLayout.Wide, window.Layout);
        Assert.NotNull(app.Items.SelectedCard); // side by side again: an item is shown

        // Edit and New open the editor pop-up; New offers every kind.
        app.Items.Detail!.EditCommand.Execute(null);
        Layout(new ItemEditorView { DataContext = app.Items.Editor });
        Pump(app.Items.Editor!.CancelCommand.ExecuteAsync(null));
        app.Items.NewCommand.Execute(VaultItemKind.Login);
        Assert.Equal(6, app.Items.Editor!.KindOptions.Count);
        Layout(window, 1280);
        Pump(app.Items.Editor!.CancelCommand.ExecuteAsync(null));
        Layout(window, 1280);

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

    [Fact]
    public void A_password_revealed_on_the_lock_screen_is_gone_and_hidden_when_the_page_is_left() => RunSta(() =>
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
        Pump(session.CreateAsync("correct horse battery staple"));
        session.Lock("test");
        Pump(Task.CompletedTask);

        var view = new VaultView { DataContext = app };
        PageLeaveGuard.FollowViewModel(view, app); // as VaultContentPage does
        Layout(view);
        Assert.True(app.IsUnlock);
        var box = (Wpf.Ui.Controls.PasswordBox)view.FindName("UnlockPasswordBox");

        void TypeAndReveal(string text)
        {
            box.Password = text;
            Assert.Equal(text, app.Password);
            box.SetCurrentValue(Wpf.Ui.Controls.PasswordBox.IsPasswordRevealedProperty, true); // the eye button
            Pump(Task.CompletedTask);
            Assert.Equal(text, box.Text); // readable on screen
        }

        void AssertEmptyAndHidden()
        {
            Assert.Equal("", app.Password);
            Assert.Equal("", box.Password);
            Assert.Equal("", box.Text);
            Assert.False(box.IsPasswordRevealed);
        }

        // 1. Typed, shown, and the page is left (another page, the tray, minimized).
        TypeAndReveal("half of my vault pass");
        PageLeaveGuard.Leave(view, app.ForgetTypedSecrets);
        Pump(Task.CompletedTask);
        AssertEmptyAndHidden();
        box.Password = "typed again"; // after coming back: dots, not the password
        Pump(Task.CompletedTask);
        Assert.DoesNotContain("typed", box.Text, StringComparison.Ordinal);
        box.Password = "";

        // 2. Typed, shown, and unlocked with it: the password must not wait in the box for the next lock screen.
        // (A shown WPF-UI box ignored the view model clearing it, so the password reappeared, readable, on the next lock.)
        TypeAndReveal("correct horse battery staple");
        Pump(app.UnlockCommand.ExecuteAsync(null));
        Assert.True(app.IsItems);
        AssertEmptyAndHidden();
        session.Lock("test");
        Pump(Task.CompletedTask);
        Assert.True(app.IsUnlock);
        AssertEmptyAndHidden();

        // 3. Typed, shown, and the vault locked meanwhile (or anything else that clears it): same.
        TypeAndReveal("something");
        app.ForgetTypedSecrets();
        Pump(Task.CompletedTask);
        AssertEmptyAndHidden();
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

    private static void Layout(FrameworkElement element, double width = 1040, double height = 700)
    {
        // A window that is never shown does not lay out; its content does.
        var target = element is Window { Content: FrameworkElement content } ? content : element;
        target.Measure(new Size(width, height));
        target.Arrange(new Rect(0, 0, width, height));
        target.UpdateLayout();
        target.UpdateLayout(); // a layout that changes with the width (VaultView) settles in a second pass
        Snapshot(element, new Size(width, height));
    }

    /// <summary>
    /// With HELM_VAULT_SNAPSHOTS set to a folder, every screen is rendered to a PNG there (in-process, off-screen: no
    /// window is shown and nothing on the desktop is captured), for checking the layout by eye.
    /// </summary>
    private static void Snapshot(FrameworkElement element, Size size)
    {
        if (Environment.GetEnvironmentVariable("HELM_VAULT_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var visual = element is Window { Content: FrameworkElement content } ? content : element;
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

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T Ancestor<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        for (var p = System.Windows.Media.VisualTreeHelper.GetParent(node); p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is T t && match(t)) return t;
        throw new InvalidOperationException($"No {typeof(T).Name} above {node}");
    }

    /// <summary>The button a click at the centre of <paramref name="element"/> would reach (WPF hit testing).</summary>
    private static System.Windows.Controls.Primitives.ButtonBase? ButtonAt(FrameworkElement element)
    {
        DependencyObject top = element;
        while (System.Windows.Media.VisualTreeHelper.GetParent(top) is { } parent) top = parent;
        var root = (UIElement)top;
        var point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), root);
        // A window that is never shown keeps the drawings of panels collapsed since (the Emergency Kit), which a shown
        // window would not hit; skip what is collapsed or not hit-test visible, as input does.
        DependencyObject? hit = null;
        System.Windows.Media.VisualTreeHelper.HitTest(root,
            d => d is UIElement { Visibility: not Visibility.Visible } or UIElement { IsHitTestVisible: false }
                ? System.Windows.Media.HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : System.Windows.Media.HitTestFilterBehavior.Continue,
            r =>
            {
                hit = r.VisualHit;
                return System.Windows.Media.HitTestResultBehavior.Stop;
            },
            new System.Windows.Media.PointHitTestParameters(point));
        for (var node = hit; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase button) return button;
        return null;
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

    // Every view test shares one WPF Application (a process may have only one).
    private static void RunSta(Action body) => WpfHost.Run(body);

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
