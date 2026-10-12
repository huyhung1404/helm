using Helm.Core.Hotkeys;
using Helm.Modules.ShortcutGuide;

namespace Helm.Tests;

public class ShortcutKeysTests
{
    [Theory]
    [InlineData("Ctrl+Shift+P", "Ctrl+Shift+P")]
    [InlineData("ctrl + shift + p", "Ctrl+Shift+P")]
    [InlineData("Shift+Ctrl+P", "Ctrl+Shift+P")]
    [InlineData("Win+Left", "Win+←")]
    [InlineData("Ctrl+PageDown", "Ctrl+PgDn")]
    [InlineData("Ctrl+Plus", "Ctrl++")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+-", "Ctrl+-")]
    [InlineData("alt+f4", "Alt+F4")]
    [InlineData("Ctrl+K Ctrl+C", "Ctrl+K Ctrl+C")]
    [InlineData("F12", "F12")]
    public void Parses_to_a_canonical_form(string text, string canonical) =>
        Assert.Equal(canonical, ShortcutKeys.Parse(text).Canonical);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    public void Rejects_what_is_not_a_shortcut(string text) =>
        Assert.False(ShortcutKeys.TryParse(text, out _, out var error) && error.Length == 0);

    [Fact]
    public void A_chord_leaves_its_first_key_and_the_next_presses_to_press()
    {
        var keys = ShortcutKeys.Parse("Ctrl+K Ctrl+C");
        Assert.Equal(HotkeyModifiers.Ctrl, keys.LeadModifiers);
        Assert.True(keys.HasLeadKey);
        var remaining = keys.RemainingCaps();
        Assert.Equal(["K"], remaining[0]);
        Assert.Equal(["Ctrl", "C"], remaining[1]);
    }

    [Fact]
    public void Modifiers_alone_have_no_lead_key()
    {
        var keys = ShortcutKeys.Parse("Shift Shift");
        Assert.False(keys.HasLeadKey);
        Assert.Equal(2, keys.Steps.Count);
    }
}

public class ShortcutCatalogTests
{
    private static readonly IReadOnlyList<CatalogApp> Catalog = ShortcutLibrary.LoadBuiltInCatalog();

    [Fact]
    public void Built_in_catalog_loads_with_windows_and_most_apps()
    {
        Assert.Contains(Catalog, a => a.Id == ShortcutLibrary.WindowsId);
        Assert.Contains(Catalog, a => a.Id == ShortcutLibrary.CommonId);
        Assert.True(Catalog.Count >= 10);
    }

    [Fact]
    public void Every_built_in_shortcut_parses_and_says_what_it_does()
    {
        foreach (var app in Catalog)
        foreach (var shortcut in app.Shortcuts)
        {
            Assert.True(ShortcutKeys.TryParse(shortcut.Keys, out _, out var error), $"{app.Id}: “{shortcut.Keys}”: {error}");
            Assert.False(string.IsNullOrWhiteSpace(shortcut.Does), $"{app.Id}: {shortcut.Keys} has no description");
        }
    }

    [Fact]
    public void Apps_have_unique_ids_unique_keys_and_bare_process_names()
    {
        Assert.Equal(Catalog.Count, Catalog.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var app in Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            var keys = app.Shortcuts.Select(s => ShortcutKeys.Parse(s.Keys).Canonical).ToList();
            var duplicates = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, $"{app.Id} lists {string.Join(", ", duplicates)} twice");
            Assert.All(app.Processes, p => Assert.False(p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), $"{app.Id}: {p}"));
        }
        var processes = Catalog.SelectMany(a => a.Processes).ToList();
        Assert.Equal(processes.Count, processes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public class ShortcutLibraryTests
{
    private static readonly IReadOnlyList<CatalogApp> Catalog =
    [
        new() { Id = "windows", Name = "Windows", Shortcuts = [new() { Keys = "Win+E", Does = "Explorer" }, new() { Keys = "Ctrl+Shift+Esc", Does = "Task Manager" }] },
        new() { Id = "common", Name = "Most apps", Shortcuts = [new() { Keys = "Ctrl+C", Does = "Copy" }, new() { Keys = "Ctrl+P", Does = "Print" }] },
        new()
        {
            Id = "vscode", Name = "Visual Studio Code", Processes = ["Code", "Cursor"],
            Shortcuts =
            [
                new() { Keys = "Ctrl+P", Does = "Go to file" },
                new() { Keys = "Ctrl+Shift+P", Does = "Command palette" },
                new() { Keys = "Ctrl+K Ctrl+C", Does = "Comment" },
                new() { Keys = "F5", Does = "Debug" },
            ],
        },
    ];

    [Fact]
    public void Finds_an_app_by_its_process_with_or_without_exe()
    {
        var library = new ShortcutLibrary(Catalog, new ShortcutGuideSettings());
        Assert.Equal("vscode", library.FindByProcess("Code.exe")?.Id);
        Assert.Equal("vscode", library.FindByProcess("cursor")?.Id);
        Assert.Null(library.FindByProcess("notepad.exe"));
        Assert.Equal(["windows", "common", "vscode"], library.Apps.Select(a => a.Id));
    }

    [Fact]
    public void Hidden_shortcuts_stay_listed_but_leave_the_guide()
    {
        var settings = new ShortcutGuideSettings { Hidden = [ShortcutLibrary.HiddenKey("vscode", ShortcutKeys.Parse("Ctrl+P"))] };
        var library = new ShortcutLibrary(Catalog, settings);
        var app = library.Find("vscode")!;
        Assert.True(app.Shortcuts.Single(s => s.Keys.Canonical == "Ctrl+P").IsHidden);
        Assert.DoesNotContain(app.Visible, s => s.Keys.Canonical == "Ctrl+P");
    }

    [Fact]
    public void Your_shortcut_replaces_a_built_in_one_with_the_same_keys()
    {
        var settings = new ShortcutGuideSettings { Shortcuts = [new() { AppId = "vscode", Keys = "ctrl+p", Does = "My quick open" }] };
        var app = new ShortcutLibrary(Catalog, settings).Find("vscode")!;
        var ctrlP = Assert.Single(app.Shortcuts, s => s.Keys.Canonical == "Ctrl+P");
        Assert.True(ctrlP.IsOwn);
        Assert.Equal("My quick open", ctrlP.Does);
    }

    [Fact]
    public void Your_apps_are_found_by_process_and_win_over_the_catalog()
    {
        var settings = new ShortcutGuideSettings
        {
            Apps = [new() { Id = "user-1", Name = "Blender", Processes = ["blender.exe"] }, new() { Id = "user-2", Name = "My Code", Processes = ["Code"] }],
            Shortcuts = [new() { AppId = "user-1", Keys = "Ctrl+Space", Does = "Maximize the area" }],
        };
        var library = new ShortcutLibrary(Catalog, settings);
        var blender = library.FindByProcess("Blender.exe")!;
        Assert.True(blender.IsOwn);
        Assert.Single(blender.Shortcuts);
        Assert.Equal("user-2", library.FindByProcess("Code.exe")?.Id);
    }

    [Fact]
    public void Guide_lists_the_app_first_and_skips_general_shortcuts_it_overrides()
    {
        var library = new ShortcutLibrary(Catalog, new ShortcutGuideSettings());
        var view = GuideContent.Build(library, "Code.exe", HotkeyModifiers.Ctrl, includeGeneral: true);

        Assert.True(view.IsKnownApp);
        Assert.Equal("Visual Studio Code", view.AppName);
        Assert.Equal(["Visual Studio Code", "Most apps"], view.Sections.Select(s => s.Title));
        Assert.Equal(["Go to file", "Comment"], view.Sections[0].Items.Select(i => i.Does));
        // Ctrl+P is VS Code's, so "Print" is not offered for it.
        Assert.Equal(["Copy"], view.Sections[1].Items.Select(i => i.Does));
        // Ctrl+Shift: Command palette and Task Manager.
        Assert.Equal(2, Assert.Single(view.Hints, h => h.Add == HotkeyModifiers.Shift).Count);
    }

    [Fact]
    public void Skipping_the_basics_leaves_out_what_everyone_knows_but_the_app_keeps_its_keys()
    {
        IReadOnlyList<CatalogApp> catalog =
        [
            new() { Id = "windows", Name = "Windows", Shortcuts = [new() { Keys = "Alt+Tab", Does = "Switch", Basic = true }, new() { Keys = "Alt+F4", Does = "Close", Basic = true }] },
            new() { Id = "common", Name = "Most apps", Shortcuts = [new() { Keys = "Ctrl+C", Does = "Copy", Basic = true }, new() { Keys = "Ctrl+S", Does = "Save", Basic = true }] },
            new() { Id = "unity", Name = "Unity", Processes = ["Unity"], Shortcuts = [new() { Keys = "Ctrl+P", Does = "Play" }, new() { Keys = "Ctrl+S", Does = "Save the scene", Basic = true }] },
        ];
        var library = new ShortcutLibrary(catalog, new ShortcutGuideSettings());

        var view = GuideContent.Build(library, "Unity.exe", HotkeyModifiers.Ctrl, includeGeneral: true, skipBasics: true);
        Assert.Equal(["Play"], view.Sections.SelectMany(s => s.Items).Select(i => i.Does));
        // Nothing but basics with Alt: the guide has nothing to show.
        Assert.True(GuideContent.Build(library, "Unity.exe", HotkeyModifiers.Alt, true, skipBasics: true).IsEmpty);

        var all = GuideContent.Build(library, "Unity.exe", HotkeyModifiers.Ctrl, includeGeneral: true, skipBasics: false);
        Assert.Equal(["Play", "Save the scene", "Copy"], all.Sections.SelectMany(s => s.Items).Select(i => i.Does));
    }

    [Fact]
    public void The_built_in_catalog_marks_copy_and_paste_as_basic()
    {
        var common = new ShortcutLibrary(ShortcutLibrary.LoadBuiltInCatalog(), new ShortcutGuideSettings()).Common;
        Assert.True(common.Shortcuts.Single(s => s.Keys.Canonical == "Ctrl+C").IsBasic);
        Assert.True(common.Shortcuts.Single(s => s.Keys.Canonical == "Ctrl+V").IsBasic);
        Assert.False(common.Shortcuts.Single(s => s.Keys.Canonical == "Ctrl+H").IsBasic);
    }

    [Fact]
    public void Guide_for_an_unknown_app_shows_only_general_shortcuts_or_nothing()
    {
        var library = new ShortcutLibrary(Catalog, new ShortcutGuideSettings());
        var view = GuideContent.Build(library, "notepad.exe", HotkeyModifiers.Win, includeGeneral: true);
        Assert.False(view.IsKnownApp);
        Assert.Equal("notepad", view.AppName);
        Assert.Equal("Windows", Assert.Single(view.Sections).Title);

        Assert.True(GuideContent.Build(library, "notepad.exe", HotkeyModifiers.Win, includeGeneral: false).IsEmpty);
    }
}

public class ShortcutEditsTests
{
    private static readonly IReadOnlyList<CatalogApp> Catalog =
    [
        new() { Id = "windows", Name = "Windows" },
        new() { Id = "common", Name = "Most apps" },
        new()
        {
            Id = "vscode", Name = "Visual Studio Code", Processes = ["Code"],
            Shortcuts =
            [
                new() { Keys = "Ctrl+P", Does = "Go to file" },
                new() { Keys = "Ctrl+B", Does = "Toggle the sidebar" },
                new() { Keys = "Ctrl+W", Does = "Close the editor", Basic = true },
            ],
        },
    ];

    private static GuideApp Code(ShortcutGuideSettings settings) => new ShortcutLibrary(Catalog, settings).Find("vscode")!;

    private static GuideShortcut Get(ShortcutGuideSettings settings, string keys) =>
        Code(settings).Shortcuts.Single(s => s.Keys.Canonical == keys);

    [Fact]
    public void Editing_a_built_in_shortcut_keeps_its_place_and_reset_brings_it_back()
    {
        var settings = new ShortcutGuideSettings();
        ShortcutEdits.Save(settings, Get(settings, "Ctrl+B"), ShortcutKeys.Parse("Ctrl+Alt+B"), "Sidebar", basic: false);

        Assert.Equal(["Ctrl+P", "Ctrl+Alt+B", "Ctrl+W"], Code(settings).Shortcuts.Select(s => s.Keys.Canonical));
        var edited = Get(settings, "Ctrl+Alt+B");
        Assert.True(edited.IsEdited);
        Assert.Equal("Sidebar", edited.Does);

        // Editing the edit changes it in place, still standing for Ctrl+B.
        ShortcutEdits.Save(settings, edited, ShortcutKeys.Parse("Ctrl+Alt+S"), "Sidebar", basic: true);
        Assert.True(Get(settings, "Ctrl+Alt+S") is { IsEdited: true, IsBasic: true });
        Assert.Single(settings.Shortcuts);

        ShortcutEdits.Reset(settings, Get(settings, "Ctrl+Alt+S"));
        Assert.Equal(["Ctrl+P", "Ctrl+B", "Ctrl+W"], Code(settings).Shortcuts.Select(s => s.Keys.Canonical));
        Assert.Empty(settings.Shortcuts);
    }

    [Fact]
    public void Marking_a_built_in_shortcut_basic_or_not_is_an_edit()
    {
        var settings = new ShortcutGuideSettings();
        ShortcutEdits.Save(settings, Get(settings, "Ctrl+W"), ShortcutKeys.Parse("Ctrl+W"), "Close the editor", basic: false);
        Assert.True(Get(settings, "Ctrl+W") is { IsBasic: false, IsEdited: true });
        ShortcutEdits.Save(settings, Get(settings, "Ctrl+P"), ShortcutKeys.Parse("Ctrl+P"), "Go to file", basic: true);
        Assert.True(Get(settings, "Ctrl+P").IsBasic);
    }

    [Fact]
    public void Deleting_a_built_in_shortcut_can_be_undone_yours_go_for_good()
    {
        var settings = new ShortcutGuideSettings();
        ShortcutEdits.Delete(settings, Get(settings, "Ctrl+P"));
        Assert.True(Get(settings, "Ctrl+P").IsHidden);
        ShortcutEdits.Restore(settings, Get(settings, "Ctrl+P"));
        Assert.False(Get(settings, "Ctrl+P").IsHidden);

        ShortcutEdits.Add(settings, "vscode", ShortcutKeys.Parse("Ctrl+Shift+Z"), "Zen", basic: false);
        ShortcutEdits.Delete(settings, Get(settings, "Ctrl+Shift+Z"));
        Assert.DoesNotContain(Code(settings).Shortcuts, s => s.Keys.Canonical == "Ctrl+Shift+Z");

        // Deleting your edit of a built-in one deletes the built-in one too (restorable).
        ShortcutEdits.Save(settings, Get(settings, "Ctrl+B"), ShortcutKeys.Parse("Ctrl+Alt+B"), "Sidebar", basic: false);
        ShortcutEdits.Delete(settings, Get(settings, "Ctrl+Alt+B"));
        Assert.True(Get(settings, "Ctrl+B").IsHidden);
    }

    [Fact]
    public void An_edit_cannot_land_on_another_of_your_shortcuts()
    {
        var settings = new ShortcutGuideSettings();
        ShortcutEdits.Add(settings, "vscode", ShortcutKeys.Parse("Ctrl+Shift+Z"), "Zen", basic: false);
        Assert.NotNull(ShortcutEdits.Check(settings, "vscode", Get(settings, "Ctrl+B"), ShortcutKeys.Parse("Ctrl+Shift+Z"), "Sidebar"));
        Assert.Null(ShortcutEdits.Check(settings, "vscode", Get(settings, "Ctrl+Shift+Z"), ShortcutKeys.Parse("Ctrl+Shift+Z"), "Zen mode"));
        Assert.NotNull(ShortcutEdits.Check(settings, "vscode", null, ShortcutKeys.Parse("Ctrl+K"), "  "));
    }
}

public class HoldTrackerTests
{
    private const int LCtrl = 0xA2, LShift = 0xA0, LWin = 0x5B, KeyC = 0x43;

    [Fact]
    public void Holding_a_modifier_alone_arms_once_and_shows()
    {
        var tracker = new HoldTracker();
        Assert.Equal(HoldAction.Arm, tracker.OnKey(LCtrl, true, 1000));
        var generation = tracker.Generation;
        // Autorepeat while held changes nothing.
        Assert.Equal(HoldAction.None, tracker.OnKey(LCtrl, true, 1030));
        Assert.Equal(HoldAction.None, tracker.OnKey(LCtrl, true, 1060));
        Assert.True(tracker.ShouldShow(generation));
        Assert.Equal(HotkeyModifiers.Ctrl, tracker.Held);
    }

    [Fact]
    public void Adding_a_modifier_while_showing_updates_and_a_key_hides_until_release()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LCtrl, true, 0);
        Assert.True(tracker.ShouldShow(tracker.Generation));
        tracker.MarkShown();

        Assert.Equal(HoldAction.Update, tracker.OnKey(LShift, true, 100));
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, tracker.Held);
        Assert.Equal(HoldAction.Hide, tracker.OnKey(KeyC, true, 200));
        Assert.True(tracker.IsSpoiled);
        // Still holding: the guide stays away.
        Assert.Equal(HoldAction.None, tracker.OnKey(LShift, false, 300));
        Assert.False(tracker.ShouldShow(tracker.Generation));
        // Everything released: the next hold works again.
        Assert.Equal(HoldAction.None, tracker.OnKey(LCtrl, false, 400));
        Assert.Equal(HoldAction.Arm, tracker.OnKey(LCtrl, true, 2000));
    }

    [Fact]
    public void A_shortcut_pressed_before_the_timer_cancels_it()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LCtrl, true, 0);
        var generation = tracker.Generation;
        tracker.OnKey(KeyC, true, 50);
        tracker.OnKey(KeyC, false, 80);
        Assert.False(tracker.ShouldShow(generation));
    }

    [Fact]
    public void A_mouse_button_cancels_the_hold()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LCtrl, true, 0);
        var generation = tracker.Generation;
        tracker.OnMouseButton();
        Assert.False(tracker.ShouldShow(generation));
    }

    [Fact]
    public void Releasing_everything_hides_the_guide()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LWin, true, 0);
        Assert.True(tracker.ShouldShow(tracker.Generation));
        tracker.MarkShown();
        Assert.Equal(HoldAction.Hide, tracker.OnKey(LWin, false, 900));
        Assert.False(tracker.IsShowing);
        Assert.Equal(HotkeyModifiers.None, tracker.Held);
    }

    [Fact]
    public void A_missed_release_does_not_stick()
    {
        // Win+L locks the PC; Win goes up on the lock screen where the hook sees nothing.
        var physicallyDown = new HashSet<int> { LWin };
        var tracker = new HoldTracker(physicallyDown.Contains);
        tracker.OnKey(LWin, true, 0);
        physicallyDown.Remove(LWin);

        physicallyDown.Add(LCtrl);
        Assert.Equal(HoldAction.Arm, tracker.OnKey(LCtrl, true, 60_000));
        Assert.Equal(HotkeyModifiers.Ctrl, tracker.Held);
    }

    [Fact]
    public void After_win_l_the_first_hold_works()
    {
        var physicallyDown = new HashSet<int> { LWin };
        var tracker = new HoldTracker(physicallyDown.Contains);
        tracker.OnKey(LWin, true, 0);
        tracker.OnKey(0x4C, true, 100); // L: a shortcut, so this hold is spoiled; then the PC locks
        physicallyDown.Clear();

        physicallyDown.Add(LCtrl);
        Assert.Equal(HoldAction.Arm, tracker.OnKey(LCtrl, true, 60_000));
        Assert.True(tracker.ShouldShow(tracker.Generation));
    }

    [Fact]
    public void A_new_press_after_a_long_silence_counts_even_if_its_release_was_missed()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LCtrl, true, 0);
        tracker.OnKey(KeyC, true, 10); // spoiled, then Ctrl's release is missed
        Assert.Equal(HoldAction.Arm, tracker.OnKey(LCtrl, true, 5_000));
        Assert.True(tracker.ShouldShow(tracker.Generation));
    }

    [Fact]
    public void The_first_autorepeat_after_a_long_keyboard_delay_is_not_a_new_press()
    {
        var tracker = new HoldTracker();
        tracker.OnKey(LCtrl, true, 0);
        Assert.True(tracker.ShouldShow(tracker.Generation));
        tracker.MarkShown();
        // Keyboard delay "long": the first repeat comes a second later.
        Assert.Equal(HoldAction.None, tracker.OnKey(LCtrl, true, 1_000));
        Assert.True(tracker.IsShowing);
    }

    [Fact]
    public void The_watchdog_hides_when_windows_says_nothing_is_held()
    {
        var physicallyDown = new HashSet<int> { LCtrl };
        var tracker = new HoldTracker(physicallyDown.Contains);
        tracker.OnKey(LCtrl, true, 0);
        Assert.True(tracker.ShouldShow(tracker.Generation));
        tracker.MarkShown();
        Assert.False(tracker.CheckReleased());
        physicallyDown.Clear();
        Assert.True(tracker.CheckReleased());
        Assert.False(tracker.IsShowing);
    }
}
