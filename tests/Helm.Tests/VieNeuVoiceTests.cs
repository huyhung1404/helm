using System.Net;
using System.Security.Cryptography;
using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Speech;
using Helm.Modules.NovelReader.Speech.VieNeu;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helm.Tests;

/// <summary>
/// The VieNeu voice around the model: long sentences read in parts, its sound at 24 kHz, the download that goes on
/// where it stopped and never keeps a damaged file, and the settings of voices that are gone.
/// </summary>
public sealed class VieNeuVoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-vieneu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_long_sentence_is_read_in_parts_cut_at_a_comma_near_the_middle()
    {
        var first = string.Join(' ', Enumerable.Repeat("Lâm Phong ngẩng đầu nhìn trời", 4));
        var second = string.Join(' ', Enumerable.Repeat("trong lòng dâng lên cảm giác khó tả", 4));
        var sentence = first + ", " + second + ".";

        var parts = VieNeuSpeechEngine.SplitLong(sentence).ToList();

        Assert.True(sentence.Length > VieNeuSpeechEngine.MaxPart);
        Assert.Equal([first + ",", second + "."], parts);
        Assert.Equal(["Ngắn thôi."], VieNeuSpeechEngine.SplitLong("  Ngắn thôi. "));
    }

    [Fact]
    public void A_sentence_with_no_comma_is_cut_at_a_space_and_every_part_fits()
    {
        var sentence = string.Join(' ', Enumerable.Repeat("một", 150));

        var parts = VieNeuSpeechEngine.SplitLong(sentence).ToList();

        Assert.All(parts, p => Assert.InRange(p.Length, 1, VieNeuSpeechEngine.MaxPart));
        Assert.Equal(sentence.Split(' ').Length, parts.Sum(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void Halving_the_rate_keeps_speech_and_removes_what_the_new_rate_cannot_hold()
    {
        const int rate = 48000;
        float[] Tone(double hz) => Enumerable.Range(0, rate).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();
        static double Rms(short[] s) => Math.Sqrt(s.Skip(100).Take(s.Length - 200).Average(x => (double)x * x)) / short.MaxValue;

        var speech = HalfRate.Convert(Tone(440));
        var tooHigh = HalfRate.Convert(Tone(18000));

        Assert.Equal(rate / 2, speech.Length);
        Assert.InRange(Rms(speech), 0.33, 0.37);
        Assert.True(Rms(tooHigh) < 0.02, $"18 kHz should be gone, RMS {Rms(tooHigh):0.000}");
    }

    [Fact]
    public async Task The_download_goes_on_where_it_stopped_and_marks_each_file_checked()
    {
        var server = new FakeServer();
        var files = new VieNeuFiles(_dir, server.Files);
        var installer = new VieNeuInstaller(files, NullLogger<VieNeuInstaller>.Instance, server);
        // Half of the first file arrived before Helm closed.
        var first = server.Files[0];
        Directory.CreateDirectory(Path.GetDirectoryName(files.PathOf(first))!);
        await File.WriteAllBytesAsync(files.PathOf(first) + ".part", server.Content(first)[..(int)(first.Size / 2)]);

        await installer.InstallAsync();

        Assert.Equal(VieNeuInstallState.Installed, installer.State);
        Assert.True(files.IsInstalled);
        Assert.Equal(first.Size / 2, server.RangeStarts[first.Url]);
        Assert.All(server.Files.Skip(1), f => Assert.False(server.RangeStarts.ContainsKey(f.Url)));
        Assert.Equal(files.TotalSize, installer.Downloaded);
        Assert.Equal(server.Content(server.Files[1]), await File.ReadAllBytesAsync(files.PathOf(server.Files[1])));
    }

    [Fact]
    public async Task A_damaged_file_is_thrown_away_and_the_voice_stays_unavailable()
    {
        var server = new FakeServer();
        server.Damage = server.Files[1].Url;
        var files = new VieNeuFiles(_dir, server.Files);
        var installer = new VieNeuInstaller(files, NullLogger<VieNeuInstaller>.Instance, server);

        await installer.InstallAsync();

        Assert.Equal(VieNeuInstallState.Failed, installer.State);
        Assert.Contains("damaged", installer.Error);
        Assert.False(files.IsInstalled);
        Assert.False(File.Exists(files.PathOf(server.Files[1]) + ".part"));
        Assert.True(files.Has(server.Files[0]));
    }

    [Fact]
    public void The_real_voice_is_pinned_to_tested_revisions_and_hashes()
    {
        Assert.All(VieNeuFiles.All, f =>
        {
            Assert.StartsWith("https://", f.Url);
            Assert.Matches("/(resolve/[0-9a-f]{40}|[0-9a-f]{40})/", f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Size > 0);
        });
        Assert.InRange(new VieNeuFiles(_dir).TotalSize, 250_000_000, 300_000_000);
    }

    [Fact]
    public void Its_voices_show_only_once_it_is_downloaded()
    {
        var installer = new VieNeuInstaller(new VieNeuFiles(_dir), NullLogger<VieNeuInstaller>.Instance, new FakeServer());
        using var engine = new VieNeuSpeechEngine(installer, NullLogger<VieNeuSpeechEngine>.Instance);

        Assert.Empty(engine.Voices);
        Assert.Equal(11, VieNeuSpeechEngine.AllVoices.Count);
        Assert.All(VieNeuSpeechEngine.AllVoices, v => Assert.True(v.IsVieNeu && v.IsFemale && v.IsVietnamese && !v.IsOnline && !v.SupportsPitch));
        Assert.Equal(SpeechCatalog.DefaultVoiceId, VieNeuSpeechEngine.DefaultVoiceId);
    }

    [Theory]
    [InlineData("edgeapp:vi-VN-HoaiMy")]
    [InlineData("edge:vi-VN-HoaiMyNeural")]
    [InlineData("local:Ngọc Huyền")]
    public void A_saved_voice_that_is_gone_becomes_Ngoc_Huyen(string saved)
    {
        var settings = new NovelReaderSettings { VoiceId = saved };

        settings.Migrate(1);

        Assert.Equal("vieneu:ngoc_huyen", settings.VoiceId);
    }

    [Fact]
    public void A_voice_still_there_is_kept()
    {
        var settings = new NovelReaderSettings { VoiceId = "android:vi-vn-x-gft-local" };

        settings.Migrate(1);

        Assert.Equal("android:vi-vn-x-gft-local", settings.VoiceId);
    }

    /// <summary>Serves three small made-up files with their hashes, with ranges, and can damage one.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _contents = [];

        public FakeServer()
        {
            var files = new List<VieNeuFile>();
            foreach (var (path, size) in new[] { ("v3/config.json", 1000), ("v3/model.onnx", 5000), ("sea_g2p.bin", 3000) })
            {
                var bytes = new byte[size];
                new Random(size).NextBytes(bytes);
                var url = "https://example.test/" + path;
                _contents[url] = bytes;
                files.Add(new VieNeuFile(path, url, size, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }
            Files = files;
        }

        public IReadOnlyList<VieNeuFile> Files { get; }

        public string? Damage { get; set; }

        public Dictionary<string, long> RangeStarts { get; } = [];

        public byte[] Content(VieNeuFile file) => _contents[file.Url];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var content = _contents[url];
            if (url == Damage) content = content.Select(b => (byte)~b).ToArray();
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            if (from > 0) RangeStarts[url] = from;
            return Task.FromResult(new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content[(int)from..]),
            });
        }
    }
}
