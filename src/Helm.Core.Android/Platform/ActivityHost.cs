using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using AndroidX.AppCompat.App;

namespace Helm.Core.Platform;

/// <summary>The result of an activity started for a result (a file or folder picker, the print dialog…).</summary>
public sealed record ActivityOutcome(Result Result, Intent? Data)
{
    public bool Ok => Result == Result.Ok;
}

/// <summary>
/// What tools need from Helm's single activity: the activity itself (pickers, window flags, biometric prompts),
/// results of activities started for a result, and when the app goes to the background or comes back. The shell's
/// MainActivity reports its lifecycle here; tools never reference Helm.App.Android.
/// </summary>
public static class ActivityHost
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<ActivityOutcome>> Pending = new();
    private static int s_nextRequest = 0x4800;

    /// <summary>The resumed activity, or null while Helm is in the background.</summary>
    public static AppCompatActivity? Current { get; private set; }

    /// <summary>The last activity that was created, even while paused (window flags must be set on it).</summary>
    public static AppCompatActivity? Latest { get; private set; }

    /// <summary>Helm went to the background (another app, the home screen, the screen turned off). UI thread.</summary>
    public static event EventHandler? Paused;

    /// <summary>Helm is in the foreground again. UI thread.</summary>
    public static event EventHandler? Resumed;

    /// <summary>A new activity exists (e.g. recreated after rotation): per-window settings must be applied again.</summary>
    public static event EventHandler<AppCompatActivity>? Created;

    public static void OnCreated(AppCompatActivity activity)
    {
        Latest = activity;
        Created?.Invoke(null, activity);
    }

    public static void OnResumed(AppCompatActivity activity)
    {
        Current = Latest = activity;
        Resumed?.Invoke(null, EventArgs.Empty);
    }

    public static void OnPaused(AppCompatActivity activity)
    {
        if (ReferenceEquals(Current, activity)) Current = null;
        Paused?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Called by MainActivity.OnActivityResult.</summary>
    public static bool OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!Pending.TryRemove(requestCode, out var pending)) return false;
        pending.TrySetResult(new ActivityOutcome(resultCode, data));
        return true;
    }

    /// <summary>Starts an activity for its result; a cancelled or failed start gives a Canceled outcome.</summary>
    public static Task<ActivityOutcome> StartForResultAsync(Intent intent)
    {
        var activity = Latest;
        if (activity is null) return Task.FromResult(new ActivityOutcome(Result.Canceled, null));
        var request = Interlocked.Increment(ref s_nextRequest);
        var pending = new TaskCompletionSource<ActivityOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[request] = pending;
        try
        {
            activity.StartActivityForResult(intent, request);
        }
        catch (ActivityNotFoundException)
        {
            Pending.TryRemove(request, out _);
            return Task.FromResult(new ActivityOutcome(Result.Canceled, null));
        }
        return pending.Task;
    }
}

/// <summary>Implemented by an Android module whose page has its own back navigation (e.g. an item open in a list).</summary>
public interface IBackHandler
{
    /// <returns>True when the page went back itself; false lets the shell go back (to Home).</returns>
    bool HandleBack();
}
