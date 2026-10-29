using Helm.Core.Desktop;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Vocab;

/// <summary>
/// Now and then (a random time within the chosen range) a card with one word pops up in the corner of the screen,
/// without taking the focus. It waits while the PC is unused, while something full-screen is in front, and while no
/// word is analyzed yet. Timer callbacks never throw; the window lives on the UI thread.
/// </summary>
public sealed class VocabPopup : IDisposable
{
    /// <summary>Nobody touched the keyboard or mouse for this long: nobody is there to read a card.</summary>
    internal static readonly TimeSpan Away = TimeSpan.FromMinutes(3);

    /// <summary>When the card could not be shown (away, full screen), the next try is this soon (or sooner with a short range).</summary>
    internal static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly ISettingsStore<VocabSettings> _settings;
    private readonly IWindowService _windows;
    private readonly IUserActivity _activity;
    private readonly IUiDispatcher _ui;
    // The page and the shell are found when a card opens a word: the shell is built from the modules, this among them.
    private readonly IServiceProvider _services;
    private readonly VocabVoice _voice;
    private readonly ILogger<VocabPopup> _logger;
    private readonly Timer _timer;
    private VocabPopupWindow? _window;
    private bool _running;
    private (int Min, int Max) _range;

    public VocabPopup(VocabStore store, VocabQuiz quiz, ISettingsStoreFactory settings, IWindowService windows, IUserActivity activity, IUiDispatcher ui,
        IServiceProvider services, VocabVoice voice, ILogger<VocabPopup> logger)
    {
        _store = store;
        _quiz = quiz;
        _settings = settings.Get<VocabSettings>(VocabIds.ModuleId);
        _windows = windows;
        _activity = activity;
        _ui = ui;
        _services = services;
        _voice = voice;
        _logger = logger;
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
        // A new range takes effect at once, not after the gap already started.
        _settings.Changed += (_, s) =>
        {
            if (_running && (s.PopupMinSeconds, s.PopupMaxSeconds) != _range) ScheduleNext();
        };
    }

    public void Start()
    {
        _running = true;
        ScheduleNext();
    }

    public void Stop()
    {
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _ui.Post(CloseWindow);
    }

    /// <summary>Shows a card right away (the settings page's "Show a word now"); false when no word is analyzed yet.</summary>
    public bool ShowNow()
    {
        if (_quiz.Next() is null) return false;
        _ui.Post(ShowCard);
        return true;
    }

    public void Dispose() => _timer.Dispose();

    private void ScheduleNext()
    {
        if (!_running) return;
        var s = _settings.Current;
        _range = (s.PopupMinSeconds, s.PopupMaxSeconds);
        var min = Math.Max(VocabViewModel.MinPopupSeconds, Math.Min(s.PopupMinSeconds, s.PopupMaxSeconds));
        var max = Math.Max(min, s.PopupMaxSeconds);
        Schedule(TimeSpan.FromSeconds(min + Random.Shared.NextDouble() * (max - min)));
    }

    private void Schedule(TimeSpan due)
    {
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void OnTimer()
    {
        try
        {
            if (!_running) return;
            if (!_settings.Current.PopupEnabled)
            {
                ScheduleNext();
                return;
            }
            var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - _activity.LastInputTick);
            if (idle > Away || _windows.IsBusyFullScreen() || _quiz.Next() is null)
            {
                Schedule(RetryAfter());
                return;
            }
            _ui.Post(ShowCard);
            ScheduleNext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the vocabulary card");
            Schedule(RetryAfter());
        }
    }

    private TimeSpan RetryAfter()
    {
        var max = TimeSpan.FromSeconds(Math.Max(VocabViewModel.MinPopupSeconds, _settings.Current.PopupMaxSeconds));
        return max < Retry ? max : Retry;
    }

    private void ShowCard()
    {
        try
        {
            // A card still on screen (not answered yet) stays as it is.
            if (_window is { IsVisible: true }) return;
            var card = new VocabCardViewModel(_store, _quiz, voice: _voice) { ShowYouGlish = _settings.Current.ShowYouGlish };
            if (!card.ShowNext()) return;
            _window = new VocabPopupWindow(card, _windows, OpenWord);
            _window.Closed += (_, _) => _window = null;
            _window.ShowCard();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the vocabulary card");
        }
    }

    /// <summary>"See the whole tree": the word open on the Words tab of Vocabulary.</summary>
    private void OpenWord(string id)
    {
        _services.GetRequiredService<VocabViewModel>().Open(id);
        _services.GetRequiredService<IShellNavigation>().ShowPage(typeof(VocabContentPage));
    }

    private void CloseWindow()
    {
        _window?.Close();
        _window = null;
    }
}
