using System.Collections.ObjectModel;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AndroidApp = Android.App.Application;
using AndroidBitmap = Android.Graphics.Bitmap;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;

namespace Helm.Modules.Vocab;

/// <summary>An app on the phone, for the list of apps the pop-up and the bubble stay out of.</summary>
public sealed partial class VocabAppRow(string package, string label, Action<VocabAppRow>? changed = null) : ObservableObject
{
    public string Package { get; } = package;

    public string Label { get; } = label;

    [ObservableProperty] private IImage? _icon;

    [ObservableProperty] private bool _isBlocked;

    internal bool Loading { get; set; }

    partial void OnIsBlockedChanged(bool value)
    {
        if (!Loading) changed?.Invoke(this);
    }
}

/// <summary>
/// The phone-only part of Vocabulary's settings page: the permissions the pop-up and the bubble need (drawing over other
/// apps; usage access once some app is blocked), "Show a word now", and the apps they stay out of, chosen from the apps
/// in the launcher. The choices are saved through <see cref="VocabSettingsViewModel"/> (device-local).
/// </summary>
public sealed partial class VocabOverlaySettingsViewModel : ObservableObject
{
    private const int IconPx = 96;

    private readonly VocabOverlay _overlay;
    private readonly Context _context = AndroidApp.Context;
    private List<VocabAppRow>? _apps;
    private int _loading;

    [ObservableProperty] private bool _canDraw;
    [ObservableProperty] private bool _hasUsageAccess;
    [ObservableProperty] private bool _isPicking;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isLoadingApps;

    public VocabOverlaySettingsViewModel(VocabSettingsViewModel settings, VocabOverlay overlay)
    {
        Settings = settings;
        _overlay = overlay;
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VocabSettingsViewModel.PopupEnabled) or nameof(VocabSettingsViewModel.BubbleEnabled))
                OnPropertyChanged(nameof(NeedsDraw));
        };
        RefreshPermissions();
        LoadBlocked();
    }

    public VocabSettingsViewModel Settings { get; }

    /// <summary>The apps the pop-up and the bubble stay out of.</summary>
    public ObservableCollection<VocabAppRow> Blocked { get; } = [];

    /// <summary>The launcher's apps matching <see cref="Search"/> (the picker).</summary>
    public ObservableCollection<VocabAppRow> Apps { get; } = [];

    public bool HasBlocked => Blocked.Count > 0;

    /// <summary>The pop-up or the bubble is on, but Helm may not draw over other apps yet.</summary>
    public bool NeedsDraw => !CanDraw && (Settings.PopupEnabled || Settings.BubbleEnabled);

    /// <summary>Some app is blocked, but Helm cannot see which app is in front.</summary>
    public bool NeedsUsageAccess => HasBlocked && !HasUsageAccess;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnCanDrawChanged(bool value) => OnPropertyChanged(nameof(NeedsDraw));

    partial void OnHasUsageAccessChanged(bool value) => OnPropertyChanged(nameof(NeedsUsageAccess));

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>Back from the phone's settings (or the page opened): what is allowed now.</summary>
    public void RefreshPermissions()
    {
        CanDraw = VocabOverlayPlatform.CanDraw(_context);
        HasUsageAccess = VocabOverlayPlatform.HasUsageAccess(_context);
        _overlay.Refresh();
    }

    [RelayCommand]
    private void OpenDrawSettings() => VocabOverlayPlatform.OpenDrawSettings(_context);

    [RelayCommand]
    private void OpenUsageSettings() => VocabOverlayPlatform.OpenUsageSettings(_context);

    [RelayCommand]
    private void ShowNow()
    {
        Message = _overlay.ShowNow() switch
        {
            VocabShowResult.NoPermission => "Allow Helm to display over other apps first (the button above).",
            VocabShowResult.NoWord => "No word is ready yet: words show once an AI agent has broken them down.",
            _ => null,
        };
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- Blocked apps --------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ChooseApps()
    {
        IsPicking = true;
        Search = "";
        if (_apps is null) await LoadAppsAsync();
        Filter();
    }

    [RelayCommand]
    private void DonePicking() => IsPicking = false;

    [RelayCommand]
    private void Unblock(VocabAppRow? row)
    {
        if (row is null) return;
        Settings.SetBlockedApps(Settings.BlockedApps.Where(p => p != row.Package));
        if (_apps?.FirstOrDefault(a => a.Package == row.Package) is { } app) Set(app, false);
        LoadBlocked();
    }

    private void OnPicked(VocabAppRow row)
    {
        var list = Settings.BlockedApps.ToList();
        list.Remove(row.Package);
        if (row.IsBlocked) list.Add(row.Package);
        Settings.SetBlockedApps(list);
        LoadBlocked();
    }

    /// <summary>The saved list as rows (an app since uninstalled still shows, by its package name, so it can be removed).</summary>
    private void LoadBlocked()
    {
        var pm = _context.PackageManager!;
        Blocked.Clear();
        foreach (var package in Settings.BlockedApps)
        {
            var known = _apps?.FirstOrDefault(a => a.Package == package);
            var row = new VocabAppRow(package, known?.Label ?? Label(pm, package)) { Icon = known?.Icon };
            if (row.Icon is null) _ = Task.Run(() => LoadIcon(pm, row));
            Blocked.Add(row);
        }
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(NeedsUsageAccess));
    }

    private async Task LoadAppsAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        IsLoadingApps = true;
        try
        {
            var pm = _context.PackageManager!;
            var own = _context.PackageName;
            var blocked = Settings.BlockedApps.ToHashSet(StringComparer.Ordinal);
            // The launcher's apps (the manifest's <queries> lets Helm see them on Android 11+).
            var found = await Task.Run(() =>
            {
                var launcher = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher)!;
                return pm.QueryIntentActivities(launcher, 0)
                    .Select(r => r.ActivityInfo?.PackageName)
                    .OfType<string>()
                    .Where(p => p != own)
                    .Distinct(StringComparer.Ordinal)
                    .Select(p => (Package: p, Label: Label(pm, p)))
                    .OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            });
            _apps = found.Select(a =>
            {
                var row = new VocabAppRow(a.Package, a.Label, OnPicked) { Loading = true };
                row.IsBlocked = blocked.Contains(a.Package);
                row.Loading = false;
                return row;
            }).ToList();
            // Icons come in as they load, so the list shows at once.
            _ = Task.Run(() =>
            {
                foreach (var row in _apps) LoadIcon(pm, row);
            });
        }
        finally
        {
            IsLoadingApps = false;
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    private void Filter()
    {
        if (_apps is null) return;
        var query = Search.Trim();
        Apps.Clear();
        foreach (var app in _apps.Where(a => query.Length == 0 || a.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Apps.Add(app);
    }

    private static void Set(VocabAppRow row, bool blocked)
    {
        row.Loading = true;
        row.IsBlocked = blocked;
        row.Loading = false;
    }

    private static string Label(PackageManager pm, string package)
    {
        try
        {
            var info = pm.GetApplicationInfo(package, 0);
            return pm.GetApplicationLabel(info) ?? package;
        }
        catch (PackageManager.NameNotFoundException)
        {
            return package;
        }
    }

    /// <summary>The app's icon as a small picture for the page (on a worker thread; set on the UI thread).</summary>
    private static void LoadIcon(PackageManager pm, VocabAppRow row)
    {
        try
        {
            var drawable = pm.GetApplicationIcon(row.Package);
            using var bitmap = AndroidBitmap.CreateBitmap(IconPx, IconPx, AndroidBitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(bitmap);
            drawable.SetBounds(0, 0, IconPx, IconPx);
            drawable.Draw(canvas);
            using var stream = new MemoryStream();
            bitmap.Compress(AndroidBitmap.CompressFormat.Png!, 100, stream);
            stream.Position = 0;
            var image = new AvaloniaBitmap(stream);
            Dispatcher.UIThread.Post(() => row.Icon = image);
        }
        catch (Exception ex) when (ex is PackageManager.NameNotFoundException or Java.Lang.Exception or ArgumentException)
        {
            // No icon: the name alone.
        }
    }
}
