using Helm.Modules.NovelReader.Speech;
using Xunit;

namespace Helm.Tests;

/// <summary>A long press on a word in study mode says its Chinese: online Xiaoxiao, an offline Chinese voice, reading aloud paused.</summary>
public sealed class NovelPronounceTests
{
    private static readonly SpeechAudio Sound = new([1, 2, 3], "audio/mpeg");

    [Fact]
    public async Task A_word_is_said_by_the_online_Mandarin_voice_slower_and_kept_for_the_next_press()
    {
        var edge = new Engine(EdgeSpeechEngine.EnginePrefix);
        var player = new Player();
        var pronouncer = new WordPronouncer([edge], player);

        Assert.Null(await pronouncer.SayAsync("林婉儿"));
        Assert.Null(await pronouncer.SayAsync("林婉儿"));

        var (text, voice, options) = Assert.Single(edge.Requests);
        Assert.Equal("林婉儿", text);
        Assert.Equal("zh-CN", voice.Language);
        Assert.True(options.Rate < 1);
        Assert.Equal(2, player.Played);
    }

    [Fact]
    public async Task Only_the_Chinese_of_a_word_is_said_and_nothing_without_it()
    {
        var edge = new Engine(EdgeSpeechEngine.EnginePrefix);
        var player = new Player();
        var pronouncer = new WordPronouncer([edge], player);

        Assert.Null(await pronouncer.SayAsync("“，…”"));
        Assert.Empty(edge.Requests);
        Assert.Null(await pronouncer.SayAsync("“说道”"));
        Assert.Equal("说道", Assert.Single(edge.Requests).Text);
    }

    [Fact]
    public async Task Once_the_online_voice_failed_words_go_straight_to_an_offline_Chinese_voice_for_a_while()
    {
        var edge = new Engine(EdgeSpeechEngine.EnginePrefix) { Fails = true };
        var windows = new Engine("win",
            new SpeechVoice("win:an", "An", "vi-VN", IsOnline: false),
            new SpeechVoice("win:huihui", "Huihui", "zh-CN", IsOnline: false));
        var player = new Player();
        var clock = new Clock();
        var pronouncer = new WordPronouncer([edge, windows], player, time: clock);

        Assert.Null(await pronouncer.SayAsync("你好"));
        Assert.Equal("win:huihui", Assert.Single(windows.Requests).Voice.Id);

        // No wait for the online voice again, and the word said offline plays from memory.
        edge.Fails = false;
        Assert.Null(await pronouncer.SayAsync("你好"));
        Assert.Null(await pronouncer.SayAsync("再见"));
        Assert.Single(edge.Requests);
        Assert.Equal(2, windows.Requests.Count);

        // After a rest the online voice is asked again, and its sound is kept from then on.
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Null(await pronouncer.SayAsync("你好"));
        Assert.Null(await pronouncer.SayAsync("你好"));
        Assert.Equal(2, edge.Requests.Count);
        Assert.Equal(2, windows.Requests.Count);
        Assert.Equal(5, player.Played);
    }

    [Fact]
    public async Task The_phones_Chinese_voice_kept_aside_stands_in_and_no_voice_at_all_says_why()
    {
        var edge = new Engine(EdgeSpeechEngine.EnginePrefix) { Fails = true };
        var phone = new PhoneEngine();
        var pronouncer = new WordPronouncer([edge, phone], new Player());

        Assert.Null(await pronouncer.SayAsync("你好"));
        Assert.Equal("android:cmn-cn-x-ccc-local", Assert.Single(phone.Requests).Voice.Id);

        var alone = new WordPronouncer([new Engine(EdgeSpeechEngine.EnginePrefix) { Fails = true }], new Player());
        Assert.Equal(WordPronouncer.NoVoice, await alone.SayAsync("你好"));
    }

    [Fact]
    public async Task Reading_aloud_pauses_while_words_are_said_and_goes_on_once_after_the_last()
    {
        var edge = new Engine(EdgeSpeechEngine.EnginePrefix);
        var player = new Player { Hold = true };
        var pronouncer = new WordPronouncer([edge], player);
        var paused = 0;
        var resumed = 0;
        pronouncer.QuietReading = () =>
        {
            paused++;
            return () => resumed++;
        };

        var first = pronouncer.SayAsync("你好");
        await player.Started.Task;
        player.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A second word pressed while the first is said cuts it.
        var second = pronouncer.SayAsync("再见");
        await player.Started.Task;
        Assert.Null(await first);
        Assert.Equal(0, resumed);

        player.Finish();
        Assert.Null(await second);
        Assert.Equal(1, paused);
        Assert.Equal(1, resumed);
    }

    [Fact]
    public void The_online_request_speaks_the_language_of_its_voice()
    {
        var options = new SpeechOptions(1, 1, 1);
        Assert.Contains("xml:lang='zh-CN'", EdgeSpeechEngine.Ssml("你好", "zh-CN-XiaoxiaoNeural", options));
        Assert.Contains("xml:lang='vi-VN'", EdgeSpeechEngine.Ssml("xin chào", "vi-VN-HoaiMyNeural", options));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private class Engine(string prefix, params SpeechVoice[] voices) : ISpeechEngine
    {
        public bool Fails { get; set; }

        public List<(string Text, SpeechVoice Voice, SpeechOptions Options)> Requests { get; } = [];

        public string Prefix => prefix;

        public IReadOnlyList<SpeechVoice> Voices => voices;

        public void RefreshVoices() { }

        public Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
        {
            Requests.Add((text, voice, options));
            return Fails ? Task.FromException<SpeechAudio>(new SpeechUnavailableException("offline")) : Task.FromResult(Sound);
        }
    }

    /// <summary>The phone's engine: Vietnamese voices listed, its Chinese one only on request.</summary>
    private sealed class PhoneEngine() : Engine("android", new SpeechVoice("android:vi-vn-x-gft-local", "Vietnamese 1", "vi-VN", IsOnline: false)),
        ILanguageVoices
    {
        public Task<SpeechVoice?> VoiceForAsync(string language, CancellationToken ct) =>
            Task.FromResult(language == "zh" ? new SpeechVoice("android:cmn-cn-x-ccc-local", "Chinese", "zh-CN", IsOnline: false) : null);
    }

    private sealed class Player : IClipPlayer
    {
        private TaskCompletionSource? _playing;

        /// <summary>Clips play until <see cref="Finish"/> (or a newer one cuts them).</summary>
        public bool Hold { get; init; }

        public int Played { get; private set; }

        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PlayAsync(SpeechAudio audio, CancellationToken ct)
        {
            Played++;
            var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _playing = playing;
            Started.TrySetResult();
            if (!Hold) return;
            using (ct.Register(() => playing.TrySetCanceled(ct)))
                await playing.Task;
        }

        public void Finish() => _playing?.TrySetResult();
    }
}
