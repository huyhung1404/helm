using System.Text;
using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Grammar;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Tests;

/// <summary>Marking a paragraph for reading by eye: sentence bands, what is said, subject, verb and object.</summary>
public sealed class NovelReaderGrammarTests
{
    private static readonly WordClassDictionary Classes = new(
    [
        ("轻轻", WordClass.Adverb), ("点头", WordClass.Verb), ("看见", WordClass.Verb), ("一位", WordClass.Number), ("女子", WordClass.Noun),
        ("在", WordClass.Preposition), ("寺里", WordClass.Noun), ("他", WordClass.Pronoun), ("跪下", WordClass.Verb), ("她", WordClass.Pronoun),
        ("惋惜地", WordClass.Adjective), ("摇头", WordClass.Verb), ("说", WordClass.Verb), ("不急", WordClass.Adjective), ("了", WordClass.Particle),
        ("拿起", WordClass.Verb), ("伞", WordClass.Noun),
    ]);

    [Fact]
    public void The_subject_verb_and_object_of_each_clause_are_found_from_the_chinese()
    {
        var line = Line(("林宛", "Lâm Uyển", SegmentKind.Name), ("轻轻", "nhẹ nhàng", SegmentKind.Phrase), ("点头", "gật đầu", SegmentKind.Phrase), (",", ",", SegmentKind.Text),
            ("看见", "thấy", SegmentKind.Phrase), ("一位", "một vị", SegmentKind.Phrase), ("女子", "nữ tử", SegmentKind.Phrase), (".", ".", SegmentKind.Text));

        var roles = Roles(line);

        Assert.Equal([GrammarRole.Subject, GrammarRole.Modifier, GrammarRole.Verb, GrammarRole.None, GrammarRole.Verb, GrammarRole.Object, GrammarRole.Object, GrammarRole.None], roles);
    }

    [Fact]
    public void A_noun_after_a_preposition_is_where_not_who_and_manner_words_do_not_hide_the_subject()
    {
        // 在寺里他跪下: "in the temple he knelt".
        var line = Line(("在", "tại", SegmentKind.Phrase), ("寺里", "trong chùa", SegmentKind.Phrase), ("他", "hắn", SegmentKind.Phrase), ("跪下", "quỳ xuống", SegmentKind.Phrase));
        Assert.Equal([GrammarRole.None, GrammarRole.None, GrammarRole.Subject, GrammarRole.Verb], Roles(line));

        // 在寺里跪下: no subject at all.
        line = Line(("在", "tại", SegmentKind.Phrase), ("寺里", "trong chùa", SegmentKind.Phrase), ("跪下", "quỳ xuống", SegmentKind.Phrase));
        Assert.Equal([GrammarRole.None, GrammarRole.None, GrammarRole.Verb], Roles(line));

        // 她惋惜地摇头: "she shook her head regretfully".
        line = Line(("她", "nàng", SegmentKind.Phrase), ("惋惜地", "tiếc nuối", SegmentKind.Phrase), ("摇头", "lắc đầu", SegmentKind.Phrase));
        Assert.Equal([GrammarRole.Subject, GrammarRole.Modifier, GrammarRole.Verb], Roles(line));

        // 他拿起了伞: the particle between the verb and the object is skipped.
        line = Line(("他", "hắn", SegmentKind.Phrase), ("拿起", "cầm lấy", SegmentKind.Phrase), ("了", "", SegmentKind.Phrase), ("伞", "ô", SegmentKind.Phrase));
        Assert.Equal(GrammarRole.Object, Roles(line)[3]);
    }

    [Fact]
    public void Sentences_alternate_and_what_is_said_is_marked()
    {
        // Nàng nói: “Không gấp.” Lâm Uyển gật đầu.
        var line = Line(("她", "Nàng", SegmentKind.Phrase), ("说", "nói", SegmentKind.Phrase), (":", ":", SegmentKind.Text), ("“", "“", SegmentKind.Text),
            ("不急", "Không gấp", SegmentKind.Phrase), (".", ".", SegmentKind.Text), ("”", "”", SegmentKind.Text),
            ("林宛", "Lâm Uyển", SegmentKind.Name), ("点头", "gật đầu", SegmentKind.Phrase), (".", ".", SegmentKind.Text));
        var sentences = SentenceSplitter.Split(line);

        var marks = GrammarTagger.Mark(line, sentences, Classes);

        Assert.Equal(2, sentences.Count);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 1, 1, 1], marks.Select(m => m.Band));
        Assert.True(marks[7].StartsSentence);
        Assert.Equal([false, false, false, true, true, true, true, false, false, false], marks.Select(m => m.Dialogue));
        Assert.Equal(GrammarRole.Subject, marks[7].Role);
    }

    [Fact]
    public void Without_the_word_classes_only_sentences_and_what_is_said_are_marked()
    {
        var line = Line(("林宛", "Lâm Uyển", SegmentKind.Name), ("点头", "gật đầu", SegmentKind.Phrase), (".", ".", SegmentKind.Text));
        var notLoaded = new WordClassDictionary(() => new MemoryStream());

        var marks = GrammarTagger.Mark(line, SentenceSplitter.Split(line), notLoaded);

        Assert.All(marks, m => Assert.Equal(GrammarRole.None, m.Role));
        Assert.All(marks, m => Assert.Equal(0, m.Band));
    }

    [Fact]
    public async Task Word_classes_are_read_from_jiebas_list_and_longer_words_take_the_class_of_their_head()
    {
        var list = "林宛 3 nr\n轻轻 5 d\n点头 9 v\n一位 3 m\n女子 7 n\n轻声 4 d\n道 9 q\n";
        var dictionary = new WordClassDictionary(() => new MemoryStream(Encoding.UTF8.GetBytes(list)));
        Assert.False(dictionary.IsReady);

        await dictionary.LoadAsync();

        Assert.True(dictionary.IsReady);
        Assert.Equal(WordClass.Name, dictionary.ClassOf("林宛"));
        Assert.Equal(WordClass.Verb, dictionary.ClassOf("轻轻点头"));
        Assert.Equal(WordClass.Noun, dictionary.ClassOf("一位女子"));
        // 轻声道: "said softly", though 道 alone is listed as a measure word.
        Assert.Equal(WordClass.Verb, dictionary.ClassOf("轻声道"));
        Assert.Equal(WordClass.Other, dictionary.ClassOf("某"));
        Assert.Equal(WordClass.Adjective, WordClassDictionary.FromTag("i"));
        Assert.Equal(WordClass.Noun, WordClassDictionary.FromTag("vn"));
    }

    [Fact]
    public async Task The_word_classes_built_into_helm_load()
    {
        var dictionary = new WordClassDictionary(NovelReaderServices.OpenWordClasses);

        await dictionary.LoadAsync();

        Assert.Equal(WordClass.Verb, dictionary.ClassOf("点头"));
        Assert.Equal(WordClass.Pronoun, dictionary.ClassOf("她"));
        Assert.Equal(WordClass.Noun, dictionary.ClassOf("油纸伞"));
    }

    /// <summary>A converted line from (Chinese, Vietnamese, kind) pieces; words are spaced, punctuation is not.</summary>
    private static ConvertedLine Line(params (string Chinese, string Text, SegmentKind Kind)[] pieces)
    {
        var source = new StringBuilder();
        var segments = new List<Segment>();
        foreach (var (chinese, text, kind) in pieces)
        {
            var before = kind != SegmentKind.Text && segments.Count > 0 && segments[^1].Text != "“" ? " " : "";
            segments.Add(new Segment(source.Length, chinese.Length, text, kind, before));
            source.Append(chinese);
        }
        return new ConvertedLine(source.ToString(), segments);
    }

    private static GrammarRole[] Roles(ConvertedLine line) =>
        GrammarTagger.Mark(line, SentenceSplitter.Split(line), Classes).Select(m => m.Role).ToArray();
}
