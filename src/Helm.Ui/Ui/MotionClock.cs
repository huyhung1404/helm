using Avalonia;
using Avalonia.Controls;

namespace Helm.Core.Ui;

/// <summary>
/// The one clock for motion a control draws itself (a jar's water, a goal's fill, confetti): a frame callback in step
/// with the screen (<see cref="TopLevel.RequestAnimationFrame"/>), asked for only while something moves. A loop ends
/// when its frame says it is at rest, when the control is hidden and when it leaves the screen; a page at rest asks for
/// no frames at all. Never drive drawing with a DispatcherTimer: it ticks out of step with the screen and goes on while
/// nothing changes. Only the control is drawn again, so keep a drawn animation in a small control of its own.
/// </summary>
public static class MotionClock
{
    /// <summary>How many loops run right now (for tests: a page at rest runs none).</summary>
    public static int Running { get; private set; }

    /// <summary>
    /// Calls <paramref name="frame"/> once per screen frame with the seconds since the last one (0 the first time, at most
    /// 0.1) until it returns false (at rest). Dispose the result to stop sooner.
    /// </summary>
    public static IMotionLoop Run(Visual owner, Func<double, bool> frame)
    {
        var loop = new Loop(owner, frame);
        loop.Next();
        return loop;
    }

    /// <summary>A running loop: dispose it to stop.</summary>
    public interface IMotionLoop : IDisposable
    {
        /// <summary>Still asking for frames.</summary>
        bool IsRunning { get; }
    }

    private sealed class Loop(Visual owner, Func<double, bool> frame) : IMotionLoop
    {
        public bool IsRunning => _running;

        private TimeSpan? _last;
        private bool _running;
        private bool _stopped;

        public void Next()
        {
            if (_stopped) return;
            if (!owner.IsEffectivelyVisible || TopLevel.GetTopLevel(owner) is not { } top)
            {
                Dispose();
                return;
            }
            if (!_running)
            {
                _running = true;
                Running++;
            }
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            if (_stopped) return;
            var seconds = _last is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.1) : 0;
            _last = now;
            if (!frame(seconds))
            {
                Dispose();
                return;
            }
            Next();
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            if (_running) Running--;
            _running = false;
        }
    }
}
