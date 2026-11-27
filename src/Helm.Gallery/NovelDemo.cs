using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Reports;
using Helm.Modules.NovelReader.Scraping;
using Helm.Modules.NovelReader.Speech;
using Helm.Modules.NovelReader.Ui;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which Novel Reader a Gallery entry shows.</summary>
internal enum NovelStage
{
    /// <summary>Four novels (one with a picture cover, one from the web with new chapters), on the PC: Scrape from web too.</summary>
    Library,
    /// <summary>The same on the phone: no scraping, new chapters asked of the PC, Novel Lens in the settings.</summary>
    LibraryPhone,
    /// <summary>No novel yet, the dictionaries in place.</summary>
    FirstRun,
    /// <summary>No novel and no dictionaries: the download first.</summary>
    NoDictionaries,
    /// <summary>A .txt file read and shown chapter by chapter before it is added.</summary>
    Adding,
    /// <summary>The novel read last, open where reading stopped.</summary>
    Reading,
    /// <summary>Reading aloud: the sentence being read highlighted, the player playing.</summary>
    ReadingAloud,
    /// <summary>Study mode: each word with its Chinese, pinyin and Hán Việt.</summary>
    Study,
    /// <summary>A name tapped: its sheet.</summary>
    Word,
    /// <summary>A word held: its sentence explained.</summary>
    Sentence,
    /// <summary>The names beside the text (a sheet on the phone).</summary>
    Names,
    /// <summary>The wrong translations beside the text (a sheet on the phone).</summary>
    Reports,
    /// <summary>A sentence reported as translated wrong, with Claude Code's suggestion.</summary>
    ReportForm,
    /// <summary>The chapters sheet.</summary>
    Chapters,
    /// <summary>The reading options sheet.</summary>
    Options,
    /// <summary>A novel's sheet from its card.</summary>
    Book,
    /// <summary>The settings sheet (the gear), PC.</summary>
    Settings,
    /// <summary>The settings sheet on the phone.</summary>
    SettingsPhone,
}

/// <summary>
/// The real Novel Reader view and view model over an in-memory library: a short xianxia novel written here (Chinese, with
/// the few dictionary entries it needs, so it converts to real Vietnamese), its names, a report of a wrong translation,
/// fake voices that never make a sound, and a fake AI that explains a held sentence.
/// </summary>
internal static class NovelDemo
{
    public static Control Create(FakeModule module, NovelStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var paths = new HelmPaths(dir);
        Directory.CreateDirectory(paths.SyncDirectory);
        var db = new SyncDatabase(paths.SyncDatabaseFile, Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray());
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var store = new NovelStore(new MemorySynced<NovelBook>(), new MemorySynced<NovelEntry>(), new MemorySynced<NovelProgress>(), blobs, TimeProvider.System);
        var settings = new SettingsStoreFactory(paths);
        var device = new Device();
        var dictionaries = new DictionaryLibrary(settings, new MemorySynced<NovelDictionary>(), blobs, device, NullLogger<DictionaryLibrary>.Instance);
        if (stage != NovelStage.NoDictionaries) WriteDictionaries(dictionaries.Folder);
        settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId).Update(s =>
        {
            s.VoiceId = "vieneu:ngoc-huyen";
            s.Mode = stage == NovelStage.Study ? Helm.Modules.NovelReader.Conversion.ConvertMode.Study : Helm.Modules.NovelReader.Conversion.ConvertMode.VietPhrase;
            s.HoverHighlight = true;
            s.ShowNamesPanel = false;
        });
        var reports = new TranslationReportStore(new MemorySynced<TranslationReport>());
        var phone = stage is NovelStage.LibraryPhone or NovelStage.SettingsPhone;
        var vm = new NovelReaderViewModel(store, dictionaries, settings, new AvaloniaUi(), new YesDialogs(), device,
            new SpeechCatalog([new DemoVoices("vieneu", phone), new DemoVoices(phone ? "android" : "win", phone)]), new SilentAudio(), new NoLauncher(), new NoClipboard(),
            NullLogger<NovelReaderViewModel>.Instance, reports: reports, captureTargets: [new DemoVocabulary()]);
        var host = new Host(phone);
        var view = new NovelReaderView(module, vm, host, new NoClipboard());

        // The library is filled before the page comes up, as the synced novels are in Helm.
        var frame = new ContentControl();
        _ = SeedAsync(stage, store, reports, settings, vm).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            frame.Content = view;
            Dispatcher.UIThread.Post(() => _ = ShowStageAsync(stage, view, vm), DispatcherPriority.Background);
        }), TaskScheduler.Default);
        return frame;
    }

    /// <summary>For the headless tests: the same page, built and shown, with the stage applied.</summary>
    public static async Task<(NovelReaderView View, NovelReaderViewModel ViewModel)> CreateAsync(FakeModule module, NovelStage stage, Window window)
    {
        var frame = (ContentControl)Create(module, stage);
        window.Content = frame;
        window.Show();
        for (var i = 0; i < 400 && frame.Content is not NovelReaderView; i++) await Task.Delay(10);
        var view = (NovelReaderView)frame.Content!;
        var vm = (NovelReaderViewModel)view.DataContext!;
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        return (view, vm);
    }

    private static async Task ShowStageAsync(NovelStage stage, NovelReaderView view, NovelReaderViewModel vm)
    {
        await view.Activation;
        var library = stage is NovelStage.Library or NovelStage.LibraryPhone or NovelStage.FirstRun or NovelStage.NoDictionaries or NovelStage.Adding
            or NovelStage.Book or NovelStage.Settings or NovelStage.SettingsPhone;
        if (library && vm.IsBookOpen) vm.ShowLibraryCommand.Execute(null);
        var first = vm.Paragraphs.Skip(1).FirstOrDefault(p => p.Line is { IsSceneBreak: false });
        switch (stage)
        {
            case NovelStage.Adding:
                var file = Path.Combine(Path.GetTempPath(), "helm-gallery", "Kiếm khách giang hồ.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, NewNovel, Encoding.UTF8);
                await vm.PrepareAddAsync(file);
                break;
            case NovelStage.ReadingAloud:
                vm.PlayPauseCommand.Execute(null);
                break;
            case NovelStage.Word when first?.Line is { } line:
                vm.OpenWord(first, line.Segments.First(s => s.Kind == Helm.Modules.NovelReader.Conversion.SegmentKind.Name));
                break;
            case NovelStage.Sentence when vm.Paragraphs.Skip(2).FirstOrDefault() is { Line: { } said } paragraph:
                vm.HoldSentence(paragraph, said.Segments.ToList().FindIndex(s => s.IsWord));
                break;
            case NovelStage.Names:
                vm.Suggestions.Add(new SuggestionRowViewModel("黑衣人", 4, "Hắc Y Nhân"));
                vm.Suggestions.Add(new SuggestionRowViewModel("子时", 2, "Tý Thời"));
                view.OpenNames();
                break;
            case NovelStage.Reports:
                view.OpenReports();
                break;
            case NovelStage.ReportForm when vm.Paragraphs.Skip(3).FirstOrDefault()?.Line is { } reported && vm.Reports is { } r:
                r.Start(reported, 0, 0);
                r.Editor.Correct = "Lâm Uyển gật đầu: “Thư của sư phụ nói, đêm nay trước giờ Tý nhất định phải đến kịp.”";
                r.Editor.Suggestion = "Lâm Uyển gật đầu: “Trong thư sư phụ dặn, đêm nay phải tới trước giờ Tý.”";
                r.Editor.SuggestionNote = "的 phrase reversed: 师父的信上说 is “in master's letter it says”";
                r.Editor.ErrorKind = "Word order";
                break;
            case NovelStage.Chapters:
                view.OpenChapters();
                break;
            case NovelStage.Options:
                view.OpenOptions();
                break;
            case NovelStage.Book when vm.Books.FirstOrDefault(b => b.HasSource) is { } card:
                view.OpenBook(card);
                break;
            case NovelStage.Settings or NovelStage.SettingsPhone:
                view.OpenSettings();
                break;
        }
    }

    // ---- The library ------------------------------------------------------------------------------------------------------

    private static async Task SeedAsync(NovelStage stage, NovelStore store, TranslationReportStore reports, SettingsStoreFactory settings, NovelReaderViewModel vm)
    {
        if (stage is NovelStage.FirstRun or NovelStage.NoDictionaries) return;
        var device = "HUNG-PC";
        var main = await store.AddBookAsync("Sơn môn dạ vũ", "son-mon-da-vu.txt", MainNovel(), 120, device);
        store.SetSource(main, "https://www.ixdzs8.com/read/58613/", autoCheck: true);
        store.MarkSourceChecked(main, 3);
        store.SaveProgress(main, 0, 3, "Pixel 8", paragraphCount: 12);

        var second = await store.AddBookAsync("Kiếm lai", "kiem-lai.txt", "第1章 惊蛰\n\n林宛点了点头。\n", 1, device);
        await store.SetCoverAsync(second, Cover());
        store.SaveProgress(second, 0, 0, device, paragraphCount: 2);
        await store.AddBookAsync("Đấu phá thương khung", "dau-pha.txt", "第1章 陨落的天才\n\n林宛点了点头。\n", 1, device);
        await store.AddBookAsync("Thế giới hoàn mỹ", "the-gioi.txt", "第1章 石村\n\n林宛点了点头。\n", 1, device);

        store.SaveEntry(main, EntryKind.Name, "林宛", "Lâm Uyển");
        store.SaveEntry(main, EntryKind.Name, "青竹", "Thanh Trúc");
        store.SaveEntry(null, EntryKind.Phrase, "子时", "giờ Tý");
        store.SaveFoundNames(main, [("师父", "Sư Phụ")]);

        reports.Add(new TranslationReport
        {
            BookId = main,
            BookTitle = "Sơn môn dạ vũ",
            Chapter = 0,
            ChapterTitle = "Chương 1 Sơn môn dạ vũ",
            Chinese = "师父的信上说，今夜子时之前必须赶到。",
            Helm = "sư phụ của thư trên nói, đêm nay giờ Tý trước khi nhất định phải đến kịp.",
            Correct = "Thư sư phụ nói đêm nay phải tới trước giờ Tý.",
            Note = "的 phrase is reversed; 之前 goes before the time",
            Suggestion = "Trong thư sư phụ dặn, đêm nay phải tới trước giờ Tý.",
            SuggestionNote = "的 phrase reversed",
            ErrorKind = "Word order",
        }, device);
        reports.Add(new TranslationReport
        {
            BookId = main,
            BookTitle = "Sơn môn dạ vũ",
            ChapterTitle = "Chương 1 Sơn môn dạ vũ",
            Chinese = "竹林沙沙作响",
            Helm = "rừng trúc xào xạc",
        }, device);

        settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId).Update(s => s.OpenBookId = main);
        await Task.CompletedTask;
        _ = vm;
    }

    /// <summary>A picture cover: a night sky over a mountain, drawn here.</summary>
    private static byte[] Cover()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(240, 340));
        using (var dc = bitmap.CreateDrawingContext())
        {
            var sky = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1B2A49"), 0), new GradientStop(Color.Parse("#C0504D"), 1) },
            };
            dc.FillRectangle(sky, new Rect(0, 0, 240, 340));
            dc.DrawEllipse(new SolidColorBrush(Color.Parse("#F5E6C8")), null, new Point(170, 80), 28, 28);
            var mountain = new StreamGeometry();
            using (var g = mountain.Open())
            {
                g.BeginFigure(new Point(0, 340), true);
                g.LineTo(new Point(70, 210));
                g.LineTo(new Point(120, 250));
                g.LineTo(new Point(180, 170));
                g.LineTo(new Point(240, 260));
                g.LineTo(new Point(240, 340));
                g.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.Parse("#101521")), null, mountain);
            dc.DrawRectangle(new SolidColorBrush(Color.Parse("#E8D9B0")), null, new Rect(112, 270, 3, 60));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private const string Chapter1 =
        "第1章 山门夜雨\n\n" +
        "林宛站在山门前，抬头望着漫天细雨。\n\n" +
        "“师姐，我们真的要上山吗？”青竹小声问道。\n\n" +
        "林宛点了点头：“师父的信上说，今夜子时之前必须赶到。”\n\n" +
        "山风吹过，竹林沙沙作响，远处传来一阵钟声。\n\n" +
        "两人撑着油纸伞，沿着石阶一步一步往上走。\n\n" +
        "青竹忽然停下脚步，指着前方：“师姐，你看那里！”\n\n" +
        "只见雨幕之中，一个黑衣人静静地站在石阶尽头，手中握着一柄长剑。\n\n" +
        "林宛心中一紧，轻声道：“不要怕，跟在我身后。”\n\n" +
        "黑衣人缓缓开口：“你们终于来了。”\n\n" +
        "*\n\n" +
        "钟声又响了三下，山门缓缓打开。\n\n";

    private static string MainNovel()
    {
        var text = new StringBuilder(Chapter1);
        text.Append("第2章 黑衣人\n\n黑衣人缓缓开口：“你们终于来了。”\n\n林宛握着长剑，轻声道：“你是谁？”\n\n");
        text.Append("第3章 竹林\n\n山风吹过，竹林沙沙作响。\n\n青竹小声问道：“师姐，我们真的要上山吗？”\n\n");
        for (var n = 4; n <= 120; n++) text.Append(CultureInfo.InvariantCulture, $"第{n}章 修行\n\n林宛点了点头。\n\n");
        return text.ToString();
    }

    /// <summary>The file of the Adding stage: two chapters, one of them new words to the dictionaries.</summary>
    private const string NewNovel =
        "第1章 江湖\n\n林宛站在山门前，抬头望着漫天细雨。\n\n两人撑着油纸伞，沿着石阶一步一步往上走。\n\n" +
        "第2章 剑客\n\n黑衣人缓缓开口：“你们终于来了。”\n\n林宛握着长剑，轻声道：“你是谁？”\n\n";

    // ---- The dictionaries: just what the novel needs ---------------------------------------------------------------

    private const string HanViet =
        "林lâm 宛uyển 站trạm 在tại 山sơn 门môn 前tiền 抬đài 头đầu 望vọng 着trứ 漫mạn 天thiên 细tế 雨vũ 师sư 姐tỷ 我ngã 们môn 真chân 的đích " +
        "要yếu 上thượng 吗ma 青thanh 竹trúc 小tiểu 声thanh 问vấn 道đạo 点điểm 了liễu 父phụ 信tín 说thuyết 今kim 夜dạ 子tử 时thì 之chi 必tất 须tu " +
        "赶cản 到đáo 风phong 吹xuy 过quá 远viễn 处xứ 传truyền 来lai 一nhất 阵trận 钟chung 两lưỡng 人nhân 撑xanh 油du 纸chỉ 伞tản 沿duyên 石thạch " +
        "阶giai 步bộ 往vãng 走tẩu 忽hốt 然nhiên 停đình 下hạ 脚cước 指chỉ 方phương 你nhĩ 看khán 那na 里lý 只chỉ 见kiến 幕mạc 中trung 个cá 黑hắc " +
        "衣y 静tĩnh 地địa 尽tận 手thủ 握ác 柄bính 长trường 剑kiếm 心tâm 紧khẩn 轻khinh 不bất 怕phạ 跟cân 身thân 后hậu 缓hoãn 开khai 口khẩu 终chung " +
        "于vu 又hựu 响hưởng 三tam 打đả 沙sa 作tác 是thị 谁thùy 修tu 行hành 第đệ 章chương 惊kinh 蛰trập 陨vẫn 落lạc 才tài 村thôn 江giang 湖hồ 客khách";

    private const string Pinyin =
        "林lín 宛wǎn 站zhàn 在zài 山shān 门mén 前qián 抬tái 头tóu 望wàng 着zhe 漫màn 天tiān 细xì 雨yǔ 师shī 姐jiě 我wǒ 们men 真zhēn 的de 要yào " +
        "上shàng 吗ma 青qīng 竹zhú 小xiǎo 声shēng 问wèn 道dào 点diǎn 了le 父fù 信xìn 说shuō 今jīn 夜yè 子zǐ 时shí 之zhī 必bì 须xū 赶gǎn 到dào " +
        "风fēng 吹chuī 过guò 远yuǎn 处chù 传chuán 来lái 一yī 阵zhèn 钟zhōng 两liǎng 人rén 撑chēng 油yóu 纸zhǐ 伞sǎn 沿yán 石shí 阶jiē 步bù " +
        "往wǎng 走zǒu 忽hū 然rán 停tíng 下xià 脚jiǎo 指zhǐ 方fāng 你nǐ 看kàn 那nà 里lǐ 只zhǐ 见jiàn 幕mù 中zhōng 个gè 黑hēi 衣yī 静jìng " +
        "地de 尽jìn 手shǒu 握wò 柄bǐng 长cháng 剑jiàn 心xīn 紧jǐn 轻qīng 不bù 怕pà 跟gēn 身shēn 后hòu 缓huǎn 开kāi 口kǒu 终zhōng 于yú 又yòu " +
        "响xiǎng 三sān 打dǎ 沙shā 作zuò 是shì 谁shéi";

    private const string VietPhrase = """
        站在=đứng ở
        山门=sơn môn
        前=trước
        抬头=ngẩng đầu
        望着=nhìn
        漫天=đầy trời
        细雨=mưa phùn
        夜雨=dạ vũ
        师姐=sư tỷ
        我们=chúng ta
        真的=thật sự
        要=muốn
        上山=lên núi
        吗=sao
        小声=nhỏ giọng
        问道=hỏi
        点了点头=gật gật đầu
        师父=sư phụ
        的=của
        信=thư
        上=trên
        说=nói
        今夜=đêm nay
        之前=trước khi
        必须=nhất định phải/phải
        赶到=đến kịp
        山风=gió núi
        吹过=thổi qua
        竹林=rừng trúc
        沙沙作响=xào xạc
        远处=nơi xa
        传来=truyền đến
        一阵=một hồi
        钟声=tiếng chuông
        两人=hai người
        撑着=che
        油纸伞=ô giấy dầu
        沿着=men theo
        石阶=bậc đá
        一步一步=từng bước từng bước
        往上走=đi lên
        忽然=bỗng nhiên
        停下=dừng lại
        脚步=bước chân
        指着=chỉ vào
        前方=phía trước
        你=ngươi
        看=xem/nhìn
        那里=chỗ kia
        只见=chỉ thấy
        雨幕=màn mưa
        之中=bên trong
        一个=một
        黑衣人=hắc y nhân/người áo đen
        静静地=lặng lẽ
        站=đứng
        尽头=cuối
        手中=trong tay
        握着=nắm
        一柄=một thanh
        长剑=trường kiếm
        心中=trong lòng
        一紧=căng thẳng
        轻声道=khẽ nói
        不要=đừng
        怕=sợ
        跟在=theo
        我=ta
        身后=sau lưng
        缓缓=chậm rãi
        开口=mở miệng
        你们=các ngươi
        终于=cuối cùng
        来了=đã tới
        又=lại
        响了=vang lên
        三下=ba tiếng
        打开=mở ra
        是谁=là ai
        修行=tu hành
        竹林=rừng trúc
        江湖=giang hồ
        剑客=kiếm khách
        惊蛰=kinh trập
        陨落的天才=thiên tài vẫn lạc
        石村=thạch thôn
        """;

    private static void WriteDictionaries(string folder)
    {
        Directory.CreateDirectory(folder);
        static string Lines(string pairs) => string.Join("\n", pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => $"{p[0]}={p[1..]}"));
        File.WriteAllText(Path.Combine(folder, "ChinesePhienAmWords.txt"), Lines(HanViet));
        var phrases = new StringBuilder(VietPhrase);
        for (var n = 1; n <= 120; n++) phrases.Append(CultureInfo.InvariantCulture, $"\n第{n}章=Chương {n}");
        File.WriteAllText(Path.Combine(folder, "VietPhrase.txt"), phrases.ToString());
        File.WriteAllText(Path.Combine(folder, "Names.txt"), "林宛=Lâm Uyển\n青竹=Thanh Trúc\n");
        File.WriteAllText(Path.Combine(folder, "Pinyin.txt"), Lines(Pinyin));
    }

    // ---- Stand-ins --------------------------------------------------------------------------------------------------

    private sealed class Device : IDeviceInfo
    {
        public string DeviceName => "HUNG-PC";
    }

    private sealed class NoLauncher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }

    /// <summary>Voices that "speak" a little silence: VieNeu's two, and the PC's or the phone's own.</summary>
    private sealed class DemoVoices(string prefix, bool phone) : ISpeechEngine
    {
        public string Prefix => prefix;

        public IReadOnlyList<SpeechVoice> Voices => prefix == "vieneu"
            ?
            [
                new SpeechVoice("vieneu:ngoc-huyen", "Ngọc Huyền", "vi-VN", false) { IsFemale = true, Description = "miền Bắc, ấm" },
                new SpeechVoice("vieneu:thu-trang", "Thu Trang", "vi-VN", false) { IsFemale = true, Description = "miền Nam, trong" },
            ]
            : phone
                ? [new SpeechVoice("android:vi-vn-x-gft", "Google tiếng Việt", "vi-VN", false) { IsFemale = true }]
                : [new SpeechVoice("win:an", "Microsoft An", "vi-VN", false), new SpeechVoice("win:zira", "Microsoft Zira", "en-US", false) { IsFemale = true }];

        public void RefreshVoices() { }

        public Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct) =>
            Task.FromResult(new SpeechAudio(new byte[64], "audio/wav"));
    }

    /// <summary>Plays each sentence for as long as nobody stops it: the player shows a sentence being read.</summary>
    private sealed class SilentAudio : IAudioOutput
    {
        public Task PlayAsync(SpeechAudio audio, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
        public void Pause() { }
        public void Resume() { }
        public double Volume { get; set; } = 1;
        public void ShowNowPlaying(string title, string subtitle) { }
        public void ClearNowPlaying() { }
        public event EventHandler<MediaButton>? MediaButton
        {
            add { }
            remove { }
        }
    }

    /// <summary>Vocabulary, as the held sentence's words go to it.</summary>
    private sealed class DemoVocabulary : ICaptureTarget
    {
        public string Id => HeldSentenceViewModel.VocabularyTarget;
        public string ModuleId => "vocab";
        public string Name => "Vocabulary";
        public string Prefix => "v";
        public string Example => "";
        public int Order => 0;
        public CapturePreview Preview(string text) => new(true, text);
        public CaptureResult Capture(string text) => new(true, "Added");
    }

    private sealed class Host(bool phone) : INovelReaderPageHost
    {
        public Task<string?> PickNovelAsync() => Task.FromResult<string?>(null);
        public Task<byte[]?> PickCoverAsync(string bookTitle) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<string>> PickDictionariesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public bool CanUseTextFiles => !phone;
        public bool CanOpenFolders => !phone;
        public bool CanAddSystemVoices => !phone;
        public bool HasPointer => !phone;
        public INovelWebScraper? Scraper { get; } = phone ? null : new DemoScraper();
        public INovelPhone? Phone { get; } = phone ? new DemoPhone() : null;
        public INovelLens? Lens { get; } = phone ? new DemoLens() : null;
    }

    private sealed class DemoPhone : INovelPhone
    {
        public void PreviewVoice(SpeechVoice voice, string text) { }
        public void InstallVoiceData() { }
        public void OpenVoiceSettings() { }
        public void OpenBatterySettings() { }
        public bool RunsFreelyInBackground => false;
    }

    private sealed class DemoLens : INovelLens
    {
        public string State => "Hidden. Banking apps can't see it. Turn it on to read Fanqie, and off again when you're done.";
        public bool IsSupported => true;
        public bool IsShown => false;
        public IReadOnlyList<string> Books { get; } = ["The novel open in Novel Reader", "Sơn môn dạ vũ", "Kiếm lai"];
        public int BookIndex { get; set; }
        public double NextTap { get; set; } = 0.9;
        public void TurnOn() { }
        public void TurnOff() { }
        public void CreateBook(string title) { }
        public void Refresh() { }
    }

    /// <summary>Scrape from web, waiting for an address or a name.</summary>
    private sealed class DemoScraper : INovelWebScraper
    {
        public string Url { get; set; } = "";
        public string StatusText => "";
        public bool HasStatus => false;
        public string BrowserStatus => "Edge, not open: Helm opens it quietly when you scrape.";
        public bool IsRunning => false;
        public bool IsIndeterminate => false;
        public double Progress => 0;
        public IEnumerable<SearchHit> Results { get; } = [];
        public bool HasResults => false;
        public bool CanReadNow => false;
        public ICommand AutoCommand { get; } = new RelayCommand(() => { });
        public ICommand PickCommand { get; } = new RelayCommand<SearchHit>(_ => { });
        public ICommand ReadNowCommand { get; } = new RelayCommand(() => { });
        public ICommand CancelCommand { get; } = new RelayCommand(() => { });
        public ICommand RetryFailedCommand { get; } = new RelayCommand(() => { });
        public ICommand ContinueCommand { get; } = new RelayCommand<BookCardViewModel>(_ => { });
        public ICommand LogInCommand { get; } = new RelayCommand<BookCardViewModel>(_ => { });
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }
    }
}
