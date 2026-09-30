using Helm.Core.Capture;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.WatchLater;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class WatchLaterTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<WatchItem> _items = new();
    private readonly WatchLaterStore _store;
    private readonly TempDir _dir = new();
    private readonly SettingsStoreFactory _settings;

    public WatchLaterTests()
    {
        _store = new WatchLaterStore(_items, _time);
        _settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
    }

    public void Dispose()
    {
        _settings.Dispose();
        _dir.Dispose();
    }

    // ---- Links ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=abc&t=42", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", WatchKind.Video)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=Zx1", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", WatchKind.Video)]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", WatchKind.Video)]
    [InlineData("youtube.com/shorts/dQw4w9WgXcQ?feature=share", "https://www.youtube.com/shorts/dQw4w9WgXcQ", WatchKind.Short)]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", WatchKind.Video)]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", WatchKind.Video)]
    public void YouTube_links_are_cleaned_up_and_share_one_key(string raw, string url, WatchKind kind)
    {
        var link = VideoLink.Parse(raw)!;

        Assert.Equal(WatchSource.YouTube, link.Source);
        Assert.Equal(url, link.Url);
        Assert.Equal(kind, link.Kind);
        Assert.Equal("youtube:dQw4w9WgXcQ", link.Key);
        Assert.Equal("dQw4w9WgXcQ", link.ExternalId);
        Assert.False(link.NeedsResolve);
    }

    [Theory]
    [InlineData("https://www.facebook.com/watch/?v=1234567890123&mibextid=abc", "https://www.facebook.com/watch/?v=1234567890123", WatchKind.Video)]
    [InlineData("https://m.facebook.com/SomePage/videos/1234567890123/?__cft__[0]=x", "https://www.facebook.com/watch/?v=1234567890123", WatchKind.Video)]
    [InlineData("https://www.facebook.com/SomePage/videos/a-title-here/1234567890123", "https://www.facebook.com/watch/?v=1234567890123", WatchKind.Video)]
    [InlineData("https://www.facebook.com/reel/1234567890123?s=yWDuG2&fs=e", "https://www.facebook.com/reel/1234567890123", WatchKind.Short)]
    [InlineData("https://web.facebook.com/video.php?v=1234567890123", "https://www.facebook.com/watch/?v=1234567890123", WatchKind.Video)]
    public void Facebook_video_and_reel_links_are_cleaned_up(string raw, string url, WatchKind kind)
    {
        var link = VideoLink.Parse(raw)!;

        Assert.Equal(WatchSource.Facebook, link.Source);
        Assert.Equal(url, link.Url);
        Assert.Equal(kind, link.Kind);
        Assert.Equal("facebook:1234567890123", link.Key);
        Assert.False(link.NeedsResolve);
    }

    [Theory]
    [InlineData("https://fb.watch/abcDEF123/", WatchKind.Video)]
    [InlineData("https://www.facebook.com/share/v/1AbCdEf/", WatchKind.Video)]
    [InlineData("https://www.facebook.com/share/r/1AbCdEf/?mibextid=x", WatchKind.Short)]
    public void Facebook_short_links_wait_for_their_redirect(string raw, WatchKind kind)
    {
        var link = VideoLink.Parse(raw)!;

        Assert.Equal(WatchSource.Facebook, link.Source);
        Assert.True(link.NeedsResolve);
        Assert.Equal(kind, link.Kind);
        Assert.Null(link.ExternalId);
        Assert.DoesNotContain("mibextid", link.Url);
    }

    [Fact]
    public void Other_links_are_kept_without_tracking_parameters()
    {
        var link = VideoLink.Parse("https://Example.com/clip?id=7&utm_source=x&fbclid=y#t=3")!;

        Assert.Equal(WatchSource.Other, link.Source);
        Assert.Equal("https://example.com/clip?id=7", link.Url);
        Assert.Equal("url:https://example.com/clip?id=7", link.Key);
    }

    [Fact]
    public void The_first_link_in_shared_text_is_found_and_the_rest_is_the_note()
    {
        const string shared = "Nấu phở ngon\nhttps://youtu.be/dQw4w9WgXcQ?si=Q1 xem cuối tuần";

        var link = VideoLink.Find(shared)!;

        Assert.Equal("youtube:dQw4w9WgXcQ", link.Key);
        Assert.Equal("Nấu phở ngon\nxem cuối tuần", VideoLink.TextAround(shared, link));
        Assert.Null(VideoLink.Find("no link here"));
        Assert.Equal("youtube:dQw4w9WgXcQ", VideoLink.Find("(see https://www.youtube.com/watch?v=dQw4w9WgXcQ).")!.Key);
    }

    [Fact]
    public void A_youtube_link_without_a_video_is_an_ordinary_link()
    {
        var link = VideoLink.Parse("https://www.youtube.com/@SomeChannel")!;

        Assert.Equal(WatchSource.Other, link.Source);
    }

    // ---- Store ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Saving_a_link_twice_moves_the_video_to_the_top_and_keeps_both_notes()
    {
        var first = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!, "for the weekend");
        _time.Advance(TimeSpan.FromMinutes(1));
        var other = _store.Add(VideoLink.Parse("https://youtu.be/aaaaaaaaaaa")!);
        _store.SetWatched(first.Id, true);
        _time.Advance(TimeSpan.FromMinutes(1));

        var again = _store.Add(VideoLink.Parse("https://www.youtube.com/shorts/dQw4w9WgXcQ")!, "shader reference");

        Assert.True(again.Existed);
        Assert.Equal(first.Id, again.Id);
        var item = _store.Get(first.Id)!;
        Assert.False(item.Watched);
        Assert.Equal("for the weekend\nshader reference", item.Note);
        Assert.Equal([first.Id, other.Id], _store.Items(WatchFilter.ToWatch).Select(i => i.Id));
        Assert.Equal(2, _items.All().Count);
    }

    [Fact]
    public void Filters_split_videos_shorts_and_watched()
    {
        var video = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        _time.Advance(TimeSpan.FromMinutes(1));
        var reel = _store.Add(VideoLink.Parse("https://www.facebook.com/reel/1234567890123")!).Id;
        _time.Advance(TimeSpan.FromMinutes(1));
        var done = _store.Add(VideoLink.Parse("https://youtu.be/bbbbbbbbbbb")!).Id;
        _store.SetWatched(done, true);

        Assert.Equal([reel, video], _store.Items(WatchFilter.ToWatch).Select(i => i.Id));
        Assert.Equal([video], _store.Items(WatchFilter.Videos).Select(i => i.Id));
        Assert.Equal([reel], _store.Items(WatchFilter.Shorts).Select(i => i.Id));
        Assert.Equal([done], _store.Items(WatchFilter.Watched).Select(i => i.Id));
        Assert.Equal([reel], _store.Items(WatchFilter.ToWatch, WatchSource.Facebook).Select(i => i.Id));
        Assert.Equal((2, 1, 1, 1), _store.Counts());
        Assert.Equal(T0.AddMinutes(2), _store.Get(done)!.WatchedAt);
    }

    [Fact]
    public void A_youtube_video_knows_its_thumbnail_right_away_and_other_links_need_no_lookup()
    {
        var yt = _store.Get(_store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id)!;
        var other = _store.Get(_store.Add(VideoLink.Parse("https://example.com/v")!).Id)!;

        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/mqdefault.jpg", yt.ThumbnailUrl);
        Assert.False(yt.MetadataDone);
        Assert.True(other.MetadataDone);
    }

    [Fact]
    public void A_short_link_that_turns_out_to_be_a_saved_video_is_merged_into_it()
    {
        var saved = _store.Add(VideoLink.Parse("https://www.facebook.com/reel/1234567890123")!, "old note").Id;
        _time.Advance(TimeSpan.FromMinutes(5));
        var shortLink = _store.Add(VideoLink.Parse("https://fb.watch/abc123/")!, "new note").Id;

        var id = _store.Resolve(shortLink, VideoLink.Parse("https://www.facebook.com/reel/1234567890123")!);

        Assert.Equal(saved, id);
        Assert.Null(_store.Get(shortLink));
        Assert.Equal("old note\nnew note", _store.Get(saved)!.Note);
        Assert.Equal(T0.AddMinutes(5), _store.Get(saved)!.AddedAt);
    }

    [Fact]
    public void A_short_link_to_a_new_video_takes_the_real_link()
    {
        var id = _store.Add(VideoLink.Parse("https://fb.watch/abc123/")!).Id;

        Assert.Equal(id, _store.Resolve(id, VideoLink.Parse("https://www.facebook.com/watch/?v=987654321012")!));
        var item = _store.Get(id)!;
        Assert.Equal("facebook:987654321012", item.Key);
        Assert.Equal("https://www.facebook.com/watch/?v=987654321012", item.Url);
    }

    [Fact]
    public void Metadata_fills_gaps_without_erasing_what_is_known()
    {
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;

        _store.ApplyMetadata(id, new VideoMetadata("Title", "Channel", null, 212), done: true);
        _store.ApplyMetadata(id, new VideoMetadata(" ", null, null, null), done: false);

        var item = _store.Get(id)!;
        Assert.Equal("Title", item.Title);
        Assert.Equal("Channel", item.Channel);
        Assert.Equal(212, item.DurationSeconds);
        Assert.NotNull(item.ThumbnailUrl);
        Assert.True(item.MetadataDone);
    }

    [Fact]
    public void A_download_request_is_cleared_when_a_pc_downloads_it()
    {
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        _store.RequestDownload(id, VideoQuality.P720);
        Assert.Equal("720", _store.Get(id)!.DownloadRequest);

        _store.MarkDownloaded(id, "HUNG-PC");

        var item = _store.Get(id)!;
        Assert.Null(item.DownloadRequest);
        Assert.Equal("HUNG-PC", item.DownloadedOn);
    }

    [Fact]
    public void Notes_that_are_too_long_are_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!, new string('x', WatchItem.MaxNoteLength + 1)));
    }

    // ---- Quick Capture -------------------------------------------------------------------------------------------

    [Fact]
    public void The_capture_target_claims_video_links_and_saves_the_rest_as_the_note()
    {
        var target = new WatchLaterCaptureTarget(_store);

        Assert.True(target.Claims("look https://www.facebook.com/reel/1234567890123"));
        Assert.False(target.Claims("https://example.com"));
        Assert.False(target.Claims("buy milk"));
        Assert.False(target.Preview("buy milk").CanSave);
        Assert.Contains("Facebook Reel", target.Preview("https://www.facebook.com/reel/1234567890123 funny").Text);

        var result = target.Capture("https://www.facebook.com/reel/1234567890123 funny");

        Assert.True(result.Saved);
        var item = _store.Items(WatchFilter.ToWatch).Single().Value;
        Assert.Equal("funny", item.Note);
        Assert.Contains("Already saved", target.Preview("https://www.facebook.com/reel/1234567890123").Text);
    }

    [Fact]
    public void Shared_text_picks_the_target_that_claims_it()
    {
        var watch = new WatchLaterCaptureTarget(_store);
        var note = new FixedTarget("note");
        IReadOnlyList<ICaptureTarget> targets = [note, watch];

        Assert.Same(watch, CaptureRouter.Suggest(targets, "https://youtu.be/dQw4w9WgXcQ", note));
        Assert.Same(note, CaptureRouter.Suggest(targets, "just a thought", note));
        Assert.Same(note, CaptureRouter.Suggest(targets, "", note));
    }

    private sealed class FixedTarget(string id) : ICaptureTarget
    {
        public string Id => id;
        public string ModuleId => id;
        public string Name => id;
        public string Prefix => id[..1];
        public string Example => "";
        public int Order => 0;
        public CapturePreview Preview(string text) => new(true, "");
        public CaptureResult Capture(string text) => new(true, "");
    }

    // ---- Web metadata --------------------------------------------------------------------------------------------

    [Fact]
    public void Open_graph_tags_are_read_in_any_attribute_order()
    {
        const string html = """
            <html><head>
            <meta content="A &amp; B" property="og:title" />
            <meta property='og:image' content='https://img/x.jpg'>
            <meta name="twitter:title" content="ignored">
            <meta itemprop="duration" content="PT1H2M3S">
            </head></html>
            """;

        var tags = HttpVideoMetadataSource.MetaTags(html);

        Assert.Equal("A & B", tags["og:title"]);
        Assert.Equal("https://img/x.jpg", tags["og:image"]);
        Assert.Equal(3723, HttpVideoMetadataSource.YouTubeDuration(html));
        Assert.Equal(212, HttpVideoMetadataSource.YouTubeDuration("""...,"lengthSeconds":"212","keywords":...""" + html));
    }

    [Theory]
    [InlineData("1.2K views · 45 reactions | Cách nấu phở | Bếp Nhà Mình", "Cách nấu phở", "Bếp Nhà Mình")]
    [InlineData("Funny cat | By Cat Page | Facebook", "Funny cat", "Cat Page")]
    [InlineData("Just a caption", "Just a caption", null)]
    [InlineData("Page Name | Facebook", "Page Name", null)]
    [InlineData("2,8 triệu lượt xem · 1,2K cảm xúc | How to share with just friends. | Facebook", "How to share with just friends.", null)]
    public void Facebook_preview_titles_are_split_into_caption_and_page(string raw, string title, string? channel)
    {
        var (t, c) = HttpVideoMetadataSource.CleanFacebookTitle(raw);

        Assert.Equal(title, t);
        Assert.Equal(channel, c);
    }

    // ---- Resolver ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_resolver_merges_sources_and_follows_short_links()
    {
        var saved = _store.Add(VideoLink.Parse("https://www.facebook.com/reel/1234567890123")!).Id;
        var shortLink = _store.Add(VideoLink.Parse("https://fb.watch/abc123/")!, "from the phone").Id;
        var web = new FakeSource(10, new VideoMetadata("Caption", null, "https://img/t.jpg", null, "https://www.facebook.com/reel/1234567890123/"));
        var ytdlp = new FakeSource(20, new VideoMetadata("Other title", "Page", null, 33));
        using var resolver = new MetadataResolver(_store, [ytdlp, web]);

        await resolver.ResolveAsync(shortLink, CancellationToken.None);

        Assert.Null(_store.Get(shortLink));
        var item = _store.Get(saved)!;
        Assert.Equal("Caption", item.Title);
        Assert.Equal("Page", item.Channel);
        Assert.Equal(33, item.DurationSeconds);
        Assert.Equal("from the phone", item.Note);
        Assert.True(item.MetadataDone);
        Assert.Equal(1, web.Calls);
    }

    [Fact]
    public async Task A_facebook_video_that_leads_to_a_reel_becomes_a_short()
    {
        var id = _store.Add(VideoLink.Parse("https://www.facebook.com/watch/?v=10153231379946729")!).Id;
        using var resolver = new MetadataResolver(_store, [new FakeSource(1, new VideoMetadata("T", null, null, null, "https://www.facebook.com/reel/10153231379946729/"))]);

        await resolver.ResolveAsync(id, CancellationToken.None);

        var item = _store.Get(id)!;
        Assert.Equal(WatchKind.Short, item.Kind);
        Assert.Equal("https://www.facebook.com/reel/10153231379946729", item.Url);
    }

    [Fact]
    public async Task A_video_without_a_title_is_tried_again_later()
    {
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        using var resolver = new MetadataResolver(_store, [new FakeSource(1, null)]);

        await resolver.ResolveAsync(id, CancellationToken.None);

        Assert.False(_store.Get(id)!.MetadataDone);
    }

    [Fact]
    public async Task The_background_resolver_fills_in_a_new_video()
    {
        using var resolver = new MetadataResolver(_store, [new FakeSource(1, new VideoMetadata("T", "C", "https://i/x.jpg", 5))]);
        var resolved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        resolver.Resolved += (_, id) => resolved.TrySetResult(id);
        resolver.Start();

        var added = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        var id = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await resolver.StopAsync();

        Assert.Equal(added, id);
        Assert.Equal("T", _store.Get(id)!.Title);
    }

    private sealed class FakeSource(int order, VideoMetadata? result) : IVideoMetadataSource
    {
        public int Calls { get; private set; }
        public int Order => order;

        public Task<VideoMetadata?> FetchAsync(WatchItem item, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    // ---- Qualities -----------------------------------------------------------------------------------------------

    [Fact]
    public void Without_ffmpeg_only_qualities_with_sound_in_one_file_can_be_downloaded()
    {
        VideoFormat[] formats =
        [
            new(360, true, true, 20_000_000),
            new(720, true, false, 60_000_000),
            new(1080, true, false, 120_000_000),
            new(null, false, true, 5_000_000),
        ];

        var without = VideoQuality.ForFormats(formats, hasFfmpeg: false);
        var with = VideoQuality.ForFormats(formats, hasFfmpeg: true);

        Assert.Equal(["1080", "720", "360", "audio"], without.Select(o => o.Id));
        Assert.Equal([false, false, true, true], without.Select(o => o.Available));
        Assert.All(with, o => Assert.True(o.Available));
        Assert.Equal("About 119.2 MB", with[0].Detail);
        Assert.Equal("360", VideoQuality.Preferred(without, "1080")!.Id);
        Assert.Equal("720", VideoQuality.Preferred(with, "720")!.Id);
    }

    [Fact]
    public void Qualities_are_labelled_with_the_height_the_video_has_and_audio_is_never_picked_by_itself()
    {
        VideoFormat[] formats = [new(240, true, false, 400_000), new(144, true, false, 200_000), new(null, false, true, 300_000)];

        var options = VideoQuality.ForFormats(formats, hasFfmpeg: false);

        Assert.Equal(["240p", "Audio only"], options.Select(o => o.Label));
        Assert.Equal("360", options[0].Id);
        Assert.Equal(VideoQuality.NeedsFfmpeg, options[0].Detail);
        Assert.Null(VideoQuality.Preferred(options, "1080"));
        Assert.Equal("audio", VideoQuality.Preferred(options, "audio")!.Id);
    }

    [Fact]
    public void Format_selectors_merge_streams_only_with_ffmpeg()
    {
        Assert.Equal("bv*[height<=720]+ba/b[height<=720]/bv*+ba/b", VideoQuality.FormatSelector("720", hasFfmpeg: true));
        Assert.Equal("b[height<=720]/b", VideoQuality.FormatSelector("720", hasFfmpeg: false));
        Assert.Equal("bv*+ba/b/bv*+ba/b", VideoQuality.FormatSelector("best", hasFfmpeg: true));
        Assert.Equal("ba[ext=m4a]/ba/b", VideoQuality.FormatSelector("audio", hasFfmpeg: false));
    }

    // ---- yt-dlp output -------------------------------------------------------------------------------------------

    [Fact]
    public void Progress_lines_give_the_downloaded_fraction()
    {
        Assert.Equal(0.25, YtDlpTools.ParseProgress("HELM:25/100/NA")!.Fraction);
        Assert.Equal(0.5, YtDlpTools.ParseProgress("[download] HELM:50/NA/100.0")!.Fraction);
        Assert.Null(YtDlpTools.ParseProgress("HELM:50/NA/NA")!.Fraction);
        Assert.Null(YtDlpTools.ParseProgress("[download] Destination: x.mp4"));
    }

    [Fact]
    public void A_probe_gives_the_details_and_formats_without_storyboards()
    {
        const string json = """
            {"title":"T","uploader":"U","thumbnail":"https://t","duration":61.6,"webpage_url":"https://w",
             "formats":[
               {"format_id":"sb0","ext":"mhtml","vcodec":"none","acodec":"none"},
               {"format_id":"18","height":360,"vcodec":"avc1","acodec":"mp4a","filesize":1000},
               {"format_id":"137","height":1080,"vcodec":"avc1","acodec":"none","filesize_approx":5000},
               {"format_id":"140","vcodec":"none","acodec":"mp4a","filesize":300},
               {"format_id":"hd","height":720}
             ]}
            """;

        var probe = YtDlpTools.ParseProbe(json);

        Assert.Equal(new VideoMetadata("T", "U", "https://t", 62, "https://w"), probe.Metadata);
        Assert.Equal(4, probe.Formats.Count);
        Assert.Contains(new VideoFormat(720, true, true, null), probe.Formats);
        Assert.Contains(new VideoFormat(null, false, true, 300), probe.Formats);
    }

    [Fact]
    public void Errors_keep_only_what_the_user_can_act_on()
    {
        Assert.Equal("Private video. Sign in if you've been granted access to this video",
            YtDlpTools.ErrorText("WARNING: x\nERROR: [youtube] dQw4w9WgXcQ: Private video. Sign in if you've been granted access to this video\n"));
        Assert.Equal("something odd", YtDlpTools.ErrorText("something odd\n"));
    }

    // ---- Playback ------------------------------------------------------------------------------------------------

    [Fact]
    public void Progress_is_remembered_and_near_the_end_the_video_counts_as_watched()
    {
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;

        Assert.False(_store.SaveProgress(id, 5, 200));
        Assert.Null(_store.Get(id)!.ResumeSeconds);
        Assert.Equal(200, _store.Get(id)!.DurationSeconds);

        Assert.False(_store.SaveProgress(id, 72.6, 200));
        Assert.Equal(72, _store.Get(id)!.ResumeSeconds);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=72s", WatchLaterFormat.ResumeUrl(_store.Get(id)!));

        Assert.True(_store.SaveProgress(id, 181, 200));
        var item = _store.Get(id)!;
        Assert.True(item.Watched);
        Assert.Null(item.ResumeSeconds);
        Assert.False(_store.SaveProgress(id, 190, 200)); // already watched
    }

    [Fact]
    public void Shorts_and_facebook_links_open_without_a_start_time()
    {
        var shortId = _store.Add(VideoLink.Parse("https://youtube.com/shorts/dQw4w9WgXcQ")!).Id;
        var fb = _store.Add(VideoLink.Parse("https://www.facebook.com/watch/?v=1234567890123")!).Id;
        _store.SaveProgress(shortId, 30, 60 * 10);
        _store.SaveProgress(fb, 30, 600);

        Assert.Equal("https://www.youtube.com/shorts/dQw4w9WgXcQ", WatchLaterFormat.ResumeUrl(_store.Get(shortId)!));
        Assert.Equal("https://www.facebook.com/watch/?v=1234567890123", WatchLaterFormat.ResumeUrl(_store.Get(fb)!));
    }

    [Fact]
    public void The_tracker_writes_sparingly_and_marks_watched_once()
    {
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        var tracker = new PlaybackTracker(_store, id);
        var marked = 0;
        tracker.MarkedWatched += (_, _) => marked++;
        var writes = 0;
        _items.Changed += (_, _) => writes++;

        for (var t = 12.0; t <= 40; t += 2) tracker.Report(t, 100); // every 2 s of playback
        Assert.InRange(writes, 2, 3); // at 12 s and 28 s (+ 40 s)
        tracker.Report(41, 100);
        tracker.Flush(); // paused
        Assert.Equal(41, _store.Get(id)!.ResumeSeconds);

        tracker.Report(91, 100);
        tracker.Report(93, 100);
        tracker.Flush();
        Assert.Equal(1, marked);
        Assert.True(_store.Get(id)!.Watched);

        tracker.Report(20, 100); // seeking back after it counted as watched
        tracker.Flush();
        Assert.Null(_store.Get(id)!.ResumeSeconds);
    }

    [Fact]
    public void The_player_page_gets_its_video_in_the_query_and_errors_in_words()
    {
        Assert.Equal("https://helm-player.local/player.html?kind=facebook&href=https%3A%2F%2Fwww.facebook.com%2Freel%2F1&start=42",
            Helm.Modules.WatchLater.Player.PlayerView.PageFor("facebook", "href", "https://www.facebook.com/reel/1", 42));
        Assert.Contains("does not let it play outside YouTube", Helm.Modules.WatchLater.Player.PlayerView.ErrorText(150, null));
        Assert.Equal("custom", Helm.Modules.WatchLater.Player.PlayerView.ErrorText(-3, "custom"));
    }

    [Fact]
    public void Speeds_go_to_three_times_and_are_set_on_the_video_elements_in_invariant_culture()
    {
        Assert.Equal([1, 1.5, 2, 2.5, 3], Helm.Modules.WatchLater.Player.PlayerView.Speeds);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("vi-VN"); // "2,5" would break the script
            var script = Helm.Modules.WatchLater.Player.PlayerView.RateScript(2.5);
            Assert.EndsWith("})(2.5);", script);
            Assert.Contains("querySelectorAll('video')", script);
            Assert.Equal("2.5×", Helm.Modules.WatchLater.Player.PlayerWindow.SpeedText(2.5));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Play_uses_helms_player_when_it_can_and_the_browser_otherwise()
    {
        var player = new FakePlayer();
        var launcher = new RecordingLauncher();
        var vm = new WatchLaterViewModel(_store, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), launcher, new FakeDownloads(),
            new ThumbnailCache(new HelmPaths(_dir.Path)), NullLogger<WatchLaterViewModel>.Instance, player);
        var yt = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.Add(VideoLink.Parse("https://example.com/clip")!);
        vm.Refresh();

        vm.OpenCommand.Execute(vm.Items.Single(r => r.Id == yt));
        vm.OpenCommand.Execute(vm.Items.Single(r => r.Id != yt));
        vm.OpenOutsideCommand.Execute(vm.Items.Single(r => r.Id == yt));

        Assert.Equal([yt], player.Played);
        Assert.Equal(["https://example.com/clip", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"], launcher.Opened);
        Assert.True(vm.HasPlayer);
    }

    private sealed class FakePlayer : IVideoPlayer
    {
        public List<string> Played { get; } = [];
        public bool CanPlay(WatchItem item) => item.Source != WatchSource.Other;
        public void Play(string id) => Played.Add(id);
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<string> Opened { get; } = [];
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) => Opened.Add(url);
        public void StartNewInstance(string? arguments = null) { }
    }

    // ---- Page ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_page_saves_a_pasted_link_with_its_note()
    {
        var vm = NewViewModel(new FakeDownloads());

        vm.NewLink = "https://youtu.be/dQw4w9WgXcQ";
        Assert.True(vm.CanAdd);
        Assert.Equal("New YouTube video", vm.AddPreview);
        vm.NewNote = "for later";
        vm.AddCommand.Execute(null);

        var row = Assert.Single(vm.Items);
        Assert.Equal("", vm.NewLink);
        Assert.Equal("for later", _store.Get(row.Id)!.Note);
        Assert.Equal("To watch (1)", vm.FilterTabs[0].Label);

        vm.NewLink = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        Assert.StartsWith("Already saved", vm.AddPreview);
    }

    [Fact]
    public void Marking_as_watched_moves_a_video_to_the_watched_filter()
    {
        var vm = NewViewModel(new FakeDownloads());
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        vm.Refresh();

        vm.ToggleWatchedCommand.Execute(vm.Items.Single());

        Assert.Empty(vm.Items);
        vm.ShowFilterCommand.Execute(vm.FilterTabs.Single(t => t.Filter == WatchFilter.Watched));
        Assert.Equal(id, vm.Items.Single().Id);
        Assert.True(vm.Items.Single().Watched);
    }

    [Fact]
    public async Task Download_offers_the_qualities_first_and_starts_the_chosen_one()
    {
        var downloads = new FakeDownloads();
        var vm = NewViewModel(downloads);
        _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!);
        vm.Refresh();
        var row = vm.Items.Single();

        await vm.DownloadCommand.ExecuteAsync(row);

        Assert.True(vm.IsPickerOpen);
        Assert.Equal(["1080", "720", "audio"], vm.PickerOptions.Select(o => o.Option.Id));
        Assert.Equal("1080", vm.PickerSelected!.Option.Id); // the default quality
        vm.PickQualityCommand.Execute(vm.PickerOptions[1]);
        vm.ConfirmDownloadCommand.Execute(null);

        Assert.False(vm.IsPickerOpen);
        Assert.Equal((row.Id, "720"), downloads.Started.Single());
    }

    [Fact]
    public async Task On_a_phone_download_asks_a_pc_and_shows_the_request()
    {
        var vm = NewViewModel(new RemoteDownloads(_store));
        var id = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!).Id;
        vm.Refresh();

        Assert.Equal("Download on PC", vm.DownloadLabel);
        await vm.DownloadCommand.ExecuteAsync(vm.Items.Single());
        Assert.Equal(VideoQuality.Presets.Count, vm.PickerOptions.Count);
        vm.ConfirmDownloadCommand.Execute(null);

        Assert.Equal("1080", _store.Get(id)!.DownloadRequest);
        var row = vm.Items.Single();
        Assert.False(row.CanDownload);
        Assert.True(row.CanCancelDownload);
        Assert.Contains("Your PC downloads it (1080p)", row.DownloadText);

        _store.MarkDownloaded(id, "HUNG-PC");
        vm.Refresh();
        Assert.Equal("Downloaded on HUNG-PC", row.DownloadText);
        Assert.True(row.CanDownload);
    }

    [Fact]
    public void Search_matches_titles_notes_and_channels_without_accents()
    {
        var vm = NewViewModel(new FakeDownloads());
        var a = _store.Add(VideoLink.Parse("https://youtu.be/dQw4w9WgXcQ")!, "công thức phở").Id;
        _store.ApplyMetadata(a, new VideoMetadata("Cooking", "Bếp", null, null), done: true);
        _store.Add(VideoLink.Parse("https://youtu.be/bbbbbbbbbbb")!, "music");
        vm.Refresh();

        vm.Search = "pho";

        Assert.Equal(a, vm.Items.Single().Id);
    }

    private WatchLaterViewModel NewViewModel(IWatchDownloads downloads) =>
        new(_store, _settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard(), new NullLauncher(), downloads,
            new ThumbnailCache(new HelmPaths(_dir.Path)), NullLogger<WatchLaterViewModel>.Instance);

    private sealed class FakeDownloads : IWatchDownloads
    {
        public List<(string Id, string Quality)> Started { get; } = [];
        public bool DownloadsHere => true;

        public Task<IReadOnlyList<QualityOption>> QualitiesAsync(WatchItem item, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<QualityOption>>([new("1080", "1080p", "", true), new("720", "720p", "", true), new("audio", "Audio only", "", true)]);

        public void Start(string id, string quality) => Started.Add((id, quality));
        public void Cancel(string id) { }
        public DownloadStatus Status(string id) => DownloadStatus.None;
        public string? FileFor(string id) => null;
        public void Forget(string id) { }
        public bool NeedsFfmpeg => false;
        public Task InstallFfmpegAsync(IProgress<double> progress, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<string>? StatusChanged { add { } remove { } }
    }

    private sealed class NullLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
