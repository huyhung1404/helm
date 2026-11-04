using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The card's model sheet (the approved demo, 2026-11-04): Auto (the PC's agents in their order, then the API), each
/// agent on a Helm PC and each family of API models opening to its versions, and the effort slider. A pick or a new
/// effort starts a new conversation, as a new model always did.
/// </summary>
public partial class AskHelmActivity
{
    private Dialog? _sheet;
    private LinearLayout? _sheetContent;

    /// <summary>A Debug build's sample sheet (adb: --ez demo_picker true), to see it on a phone with no key or agent.</summary>
    private AssistantPicker? _demo;

    private AssistantPicker Picker() => _demo ?? _assistant!.Picker();
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    /// <summary>The header's chip: where it runs (dot: PC green, API amber, Auto accent), what, how hard.</summary>
    private void ShowModel()
    {
        if (_assistant is null) return;
        var picker = Picker();
        var none = _demo is null && _assistant.Current() is null;
        var effort = none ? "" : AiEfforts.Nearest(picker.Effort, picker.Efforts);
        var what = picker.Picked switch
        {
            null => none ? "No model" : "Auto",
            { Kind: AssistantPickKind.Pc } p => $"PC · {(p.Model.Length > 0 ? p.Name : p.Agent)}",
            var p => $"API · {p.Name}",
        };
        _model.Text = effort.Length > 0 ? $"{what} · {AiEfforts.Name(effort)} ▾" : $"{what} ▾";
        var dot = new GradientDrawable();
        dot.SetShape(ShapeType.Oval);
        dot.SetColor(KindColor(picker.Picked?.Kind ?? AssistantPickKind.Auto));
        dot.SetSize(Dp(8), Dp(8));
        _model.SetCompoundDrawablesRelativeWithIntrinsicBounds(dot, null, null, null);
        _model.CompoundDrawablePadding = Dp(6);
        _model.SetMaxWidth(Dp(230));
        _model.SetSingleLine(true);
        _model.Ellipsize = TextUtils.TruncateAt.End;
    }

    private Color KindColor(AssistantPickKind kind) => kind switch
    {
        AssistantPickKind.Pc => _colors.Pc,
        AssistantPickKind.Api => _colors.Api,
        _ => _colors.Accent,
    };

    private void PickModel()
    {
        if (_assistant is null || _busy) return;
        var picker = Picker();
        if (picker.Families.Count == 0)
        {
            Toast.MakeText(this, "No model yet: add a Claude or Grok key on the PC (AI → Models & keys), or an agent (AI → Proxy).", ToastLength.Long)?.Show();
            return;
        }
        // The family of the pick opens with it, so its version shows.
        if (picker.Picked is { } picked && picker.Families.FirstOrDefault(f => f.Versions.Contains(picked)) is { } family) _open.Add(KeyOf(family));
        var before = (_assistant.Choice.Route, _assistant.Choice.Agent, _assistant.Choice.Model, _assistant.Choice.Effort);
        var sheet = new Dialog(this);
        sheet.RequestWindowFeature((int)WindowFeatures.NoTitle);
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        sheet.SetContentView(content);
        sheet.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        sheet.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        sheet.Window?.SetGravity(GravityFlags.Bottom);
        sheet.DismissEvent += (_, _) =>
        {
            _sheet = null;
            _sheetContent = null;
            if (_demo is not null)
            {
                ShowModel();
                return;
            }
            var after = (_assistant.Choice.Route, _assistant.Choice.Agent, _assistant.Choice.Model, _assistant.Choice.Effort);
            if (after == before) return;
            // Another model, or another effort, is another conversation.
            _session?.End();
            _session = _assistant.Start([new PhoneTools(this)], _context);
            _session.Warm();
            ShowModel();
        };
        _sheet = sheet;
        _sheetContent = content;
        FillSheet(content);
        sheet.Show();
    }

    private void FillSheet(LinearLayout content)
    {
        content.RemoveAllViews();
        var picker = Picker();
        content.SetPadding(0, Dp(8), 0, Dp(16));
        var shape = new GradientDrawable();
        shape.SetColor(_colors.Card);
        shape.SetCornerRadii([Dp(26), Dp(26), Dp(26), Dp(26), 0, 0, 0, 0]);
        content.Background = shape;

        var grab = new View(this) { Background = Rounded(_colors.Line, 2) };
        content.AddView(grab, new LinearLayout.LayoutParams(Dp(36), Dp(4)) { Gravity = GravityFlags.CenterHorizontal, BottomMargin = Dp(8) });

        var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetPadding(Dp(20), 0, Dp(20), Dp(6));
        head.AddView(Text("Ai trả lời?", 16, _colors.Text, bold: true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        head.AddView(Text("Đổi model là bắt đầu hội thoại mới", 12, _colors.Secondary));
        content.AddView(head);

        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        list.SetPadding(Dp(10), 0, Dp(10), Dp(8));
        var scroll = new ScrollView(this);
        scroll.AddView(list);
        var maxHeight = (int)((Resources?.DisplayMetrics?.HeightPixels ?? 2000) * 0.6);
        content.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        scroll.ViewTreeObserver!.GlobalLayout += (_, _) =>
        {
            if (scroll.Height > maxHeight) scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, maxHeight);
        };

        list.AddView(AutoRow(picker));
        foreach (var (kind, label, cost) in new[] { (AssistantPickKind.Pc, "PROXY · TRÊN PC", "miễn phí"), (AssistantPickKind.Api, "API · TRẢ THEO CÂU", "có tốn tiền") })
        {
            var families = picker.Families.Where(f => f.Kind == kind).ToList();
            if (families.Count == 0) continue;
            list.AddView(GroupHeader(label, cost));
            foreach (var family in families) AddFamily(list, family, picker);
        }

        content.AddView(new View(this) { Background = new ColorDrawable(_colors.Line) }, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(1)));
        content.AddView(EffortBox(picker));
    }

    private View AutoRow(AssistantPicker picker)
    {
        var on = picker.Picked is null;
        var row = PickRow(on);
        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var title = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        title.SetGravity(GravityFlags.CenterVertical);
        title.AddView(Text("Auto", 15, _colors.Text, bold: true));
        var badge = Text("nên dùng", 11, _colors.Accent, bold: true);
        badge.SetPadding(Dp(7), Dp(1), Dp(7), Dp(1));
        // On the selected row's own tint, the badge takes the plain row colour so it still shows.
        badge.Background = Rounded(on ? _colors.Row : _colors.Bubble, 8);
        title.AddView(badge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
        texts.AddView(title);
        texts.AddView(Text("Proxy trên PC trước, hết proxy thì tới API", 13, _colors.Secondary));
        // The steps in order: green on a PC, amber on the API, struck through when it would be passed over now.
        var chain = new SpannableStringBuilder();
        foreach (var step in picker.Auto)
        {
            if (chain.Length() > 0) chain.Append("  →  ");
            var start = chain.Length();
            chain.Append($"{(step.Kind == AssistantPickKind.Pc ? "PC" : "API")} · {step.Name}");
            var color = step.Problem is null ? KindColor(step.Kind) : _colors.Secondary;
            chain.SetSpan(new ForegroundColorSpan(color), start, chain.Length(), SpanTypes.ExclusiveExclusive);
            if (step.Problem is not null) chain.SetSpan(new StrikethroughSpan(), start, chain.Length(), SpanTypes.ExclusiveExclusive);
        }
        var steps = new TextView(this) { TextSize = 12 };
        steps.SetText(chain, TextView.BufferType.Spannable);
        texts.AddView(steps, Margins(0, 4, 0, 0));
        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(Check(on));
        row.ContentDescription = "Auto" + (on ? ", đang chọn" : "");
        row.Click += (_, _) => Pick(null);
        return row;
    }

    private void AddFamily(LinearLayout list, AssistantPickFamily family, AssistantPicker picker)
    {
        var key = KeyOf(family);
        var blocked = family.Problem is not null;
        var chosen = family.Versions.FirstOrDefault(v => v == picker.Picked);
        var open = _open.Contains(key) && !blocked;
        var row = PickRow(chosen is not null);
        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        texts.AddView(Text(family.Name, 15, blocked ? _colors.Secondary : _colors.Text, bold: true));
        var status = family.Kind == AssistantPickKind.Pc
            ? $"{family.Device} · {(family.Problem is { } why ? why.TrimEnd('.') : "sẵn sàng")}"
            : $"{family.Versions.Count} phiên bản";
        var desc = new SpannableStringBuilder();
        if (chosen is not null)
        {
            desc.Append(chosen.Model.Length > 0 ? chosen.Name : "đang chọn");
            desc.SetSpan(new ForegroundColorSpan(_colors.Accent), 0, desc.Length(), SpanTypes.ExclusiveExclusive);
            desc.Append(" · ");
        }
        desc.Append(status);
        var line = new TextView(this) { TextSize = 13 };
        line.SetTextColor(_colors.Secondary);
        line.SetText(desc, TextView.BufferType.Spannable);
        texts.AddView(line);
        row.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var chevron = Text(open ? "▴" : "▾", 16, _colors.Secondary);
        row.AddView(chevron);
        if (blocked) row.Alpha = 0.55f;
        row.ContentDescription = $"{family.Name}, {status}" + (open ? ", đang mở" : "");
        list.AddView(row, Margins(0, 2, 0, 0));

        // One version and nothing to choose (Codex): a tap picks it.
        if (family.Versions.Count == 1 && family.Versions[0].Model.Length == 0)
        {
            row.Click += (_, _) => { if (!blocked) Pick(family.Versions[0]); };
            return;
        }
        // Its versions, under a thin line down their left.
        var wrap = new LinearLayout(this) { Orientation = Orientation.Horizontal, Visibility = open ? ViewStates.Visible : ViewStates.Gone };
        wrap.AddView(new View(this) { Background = new ColorDrawable(_colors.Line) }, new LinearLayout.LayoutParams(Dp(2), ViewGroup.LayoutParams.MatchParent));
        var versions = new LinearLayout(this) { Orientation = Orientation.Vertical };
        versions.SetPadding(Dp(10), 0, 0, Dp(4));
        foreach (var version in family.Versions) versions.AddView(VersionRow(version, version == picker.Picked), Margins(0, 2, 0, 0));
        wrap.AddView(versions, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        list.AddView(wrap, Margins(18, 0, 0, 0));
        row.Click += (_, _) =>
        {
            if (blocked) return;
            var now = !_open.Remove(key);
            if (now) _open.Add(key);
            wrap.Visibility = now ? ViewStates.Visible : ViewStates.Gone;
            chevron.Text = now ? "▴" : "▾";
        };
    }

    private View VersionRow(AssistantPick version, bool on)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal, Clickable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(12), Dp(8), Dp(10), Dp(8));
        row.SetMinimumHeight(Dp(48));
        row.Background = on ? Rounded(_colors.Bubble, 12) : null;
        row.AddView(Text(version.Name, 14, _colors.Text), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(Text(version.Kind == AssistantPickKind.Pc ? "miễn phí" : version.Price ?? "", 12, _colors.Secondary));
        row.AddView(Check(on), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(10) });
        row.ContentDescription = version.Name + (on ? ", đang chọn" : "");
        row.Click += (_, _) => Pick(version);
        return row;
    }

    private View EffortBox(AssistantPicker picker)
    {
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Dp(20), Dp(12), Dp(20), 0);
        var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        top.AddView(Text("Mức suy nghĩ", 14, _colors.Text, bold: true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var note = Text("", 13, _colors.Secondary);
        top.AddView(note);
        box.AddView(top);

        var slider = new EffortSlider(this, _colors.Accent, _colors.Line, _colors.Secondary, _colors.Card);
        box.AddView(slider, Margins(0, 4, 0, 0));
        var labels = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var names = AiEfforts.Levels.Select(l => Text(AiEfforts.Name(l), 11, _colors.Secondary, bold: true)).ToList();
        for (var i = 0; i < names.Count; i++)
        {
            names[i].Gravity = i == 0 ? GravityFlags.Start : i == names.Count - 1 ? GravityFlags.End : GravityFlags.CenterHorizontal;
            labels.AddView(names[i], new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
        box.AddView(labels);

        void Show(string effort)
        {
            var taken = picker.Efforts;
            var level = AiEfforts.Nearest(effort, taken);
            note.Text = taken.Count == 0 ? "Model này không chỉnh được" : $"{AiEfforts.Name(level)} · {Meaning(level)}";
            for (var i = 0; i < names.Count; i++)
            {
                var l = AiEfforts.Levels[i];
                names[i].SetTextColor(l == level ? _colors.Accent : _colors.Secondary);
                names[i].Alpha = taken.Contains(l) ? 1f : 0.4f;
            }
        }
        slider.Show(picker.Effort, picker.Efforts);
        Show(picker.Effort);
        slider.Moved += Show;
        slider.Chosen += level =>
        {
            if (_demo is not null) _demo = _demo with { Effort = level };
            else _assistant!.ChooseEffort(level);
        };
        return box;
    }

    private static string Meaning(string level) => level switch
    {
        "low" => "nhanh nhất",
        "medium" => "cân bằng",
        "high" => "nghĩ kỹ",
        "xhigh" => "nghĩ rất kỹ, chậm",
        "max" => "tối đa, chậm nhất",
        _ => "",
    };

    private void Pick(AssistantPick? pick)
    {
        if (_demo is not null) _demo = _demo with { Picked = pick, Efforts = pick?.Efforts ?? AiEfforts.Levels };
        else _assistant!.Choose(pick);
        // The check moves at once; the sheet closes a moment later, so the pick is seen.
        if (_sheetContent is { } content) FillSheet(content);
        _handler.PostDelayed(() => _sheet?.Dismiss(), 180);
    }

    private LinearLayout PickRow(bool on)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        row.SetMinimumHeight(Dp(56));
        row.Background = on ? Rounded(_colors.Bubble, 14) : null;
        return row;
    }

    private TextView Check(bool on)
    {
        var check = Text("✓", 18, _colors.Accent, bold: true);
        check.Visibility = on ? ViewStates.Visible : ViewStates.Invisible;
        check.SetPadding(Dp(8), 0, 0, 0);
        return check;
    }

    private View GroupHeader(string label, string cost)
    {
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetPadding(Dp(12), Dp(14), Dp(12), Dp(4));
        var left = Text(label, 11, _colors.Secondary, bold: true);
        left.LetterSpacing = 0.08f;
        header.AddView(left, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        header.AddView(Text(cost, 11, _colors.Secondary));
        return header;
    }

    private static string KeyOf(AssistantPickFamily family) => $"{family.Kind}:{family.Name}";

    /// <summary>The sample sheet of a Debug build: two agents (one signed out) and three API families.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void ReadDemo(Android.Content.Intent? intent)
    {
        if (intent?.GetBooleanExtra("demo_picker", false) != true) return;
        AssistantPick Pc(string agent, string model, string kind) =>
            new(AssistantPickKind.Pc, agent, model.StartsWith("grok-", StringComparison.Ordinal) ? AiProviders.Grok : AiProviders.Claude, model, AssistantPicks.Name(model))
            { Efforts = AiEfforts.For(model, kind) };
        AssistantPick Api(string model, string price) =>
            new(AssistantPickKind.Api, null, model.StartsWith("grok-", StringComparison.Ordinal) ? AiProviders.Grok : AiProviders.Claude, model, AssistantPicks.Name(model))
            { Price = price, Efforts = AiEfforts.For(model) };
        _demo = new AssistantPicker(
            [new(AssistantPickKind.Pc, "Grok · Grok 4.7 build fast", null), new(AssistantPickKind.Pc, "Claude Code · Haiku 5.5", "signed out"), new(AssistantPickKind.Api, "Haiku 5.5", null)],
            [
                new(AssistantPickKind.Pc, "Grok", [Pc("Grok", "grok-5", "grok"), Pc("Grok", "grok-4.7", "grok"), Pc("Grok", "grok-4.7-build-fast", "grok")]) { Device = "PC-OFFICE" },
                new(AssistantPickKind.Pc, "Claude Code", [Pc("Claude Code", "claude-opus-5-5", "claude-code")]) { Device = "PC-OFFICE", Problem = "Chưa đăng nhập" },
                new(AssistantPickKind.Api, "Claude Opus", [Api("claude-opus-5-5", "~420đ"), Api("claude-opus-5", "~520đ"), Api("claude-opus-4-8", "~520đ"), Api("claude-opus-4-6", "~520đ")]),
                new(AssistantPickKind.Api, "Claude Sonnet", [Api("claude-sonnet-5-5", "~210đ"), Api("claude-sonnet-4-6", "~350đ")]),
                new(AssistantPickKind.Api, "Claude Haiku", [Api("claude-haiku-5-5", "~11đ"), Api("claude-haiku-4-5", "~120đ")]),
            ],
            null, "high");
    }
}
