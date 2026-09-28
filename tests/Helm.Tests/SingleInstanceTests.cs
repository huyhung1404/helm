using Helm.Core.Services;
using Xunit;

namespace Helm.Tests;

public class SingleInstanceTests
{
    private static string UniqueName() => "HelmTest." + Guid.NewGuid().ToString("N");

    /// <summary>A named mutex is reentrant on its owning thread, so a "later launch" must acquire on another thread.</summary>
    private static SingleInstance AcquireElsewhere(string name)
    {
        SingleInstance? result = null;
        var thread = new Thread(() => result = SingleInstance.Acquire(name));
        thread.Start();
        thread.Join();
        return result!;
    }

    [Fact]
    public void Second_acquire_is_not_the_first_instance()
    {
        var name = UniqueName();
        using var first = SingleInstance.Acquire(name);
        using var second = AcquireElsewhere(name);
        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void Only_the_owner_answers_a_launch_even_when_others_listen()
    {
        // An --allow-multiple dev copy used to listen too, and the auto-reset event woke whichever Windows picked.
        var name = UniqueName();
        using var owner = SingleInstance.Acquire(name);
        using var other = AcquireElsewhere(name);
        int ownerCount = 0, otherCount = 0;
        using var ownerHit = new AutoResetEvent(false);
        // The other copy starts listening first, as the dev copies did; Windows tends to wake the oldest waiter.
        other.ListenForActivation(() => Interlocked.Increment(ref otherCount));
        owner.ListenForActivation(() => { Interlocked.Increment(ref ownerCount); ownerHit.Set(); });

        for (var i = 0; i < 20; i++)
        {
            using var launch = AcquireElsewhere(name);
            launch.SignalFirstInstance();
            Assert.True(ownerHit.WaitOne(TimeSpan.FromSeconds(5)), $"launch {i} did not reach the owner");
        }
        Assert.Equal(20, ownerCount);
        Assert.Equal(0, otherCount);
    }

    [Fact]
    public void Detached_instance_does_not_keep_the_real_one_from_being_first()
    {
        var name = UniqueName();
        using var devCopy = SingleInstance.Detached();
        devCopy.ListenForActivation(() => throw new InvalidOperationException("a detached instance must not listen"));
        devCopy.SignalFirstInstance();
        using var installed = SingleInstance.Acquire(name);
        Assert.False(devCopy.IsFirstInstance);
        Assert.True(installed.IsFirstInstance);
    }
}
