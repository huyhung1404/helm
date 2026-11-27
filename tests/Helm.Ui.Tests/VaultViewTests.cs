using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Ui;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Ui.Tests;

/// <summary>The shared Vault page (Helm.Modules.Vault.UI) over a real vault in a scratch folder (the Gallery's demo).</summary>
public sealed class VaultViewTests
{
    private static (Window Window, VaultView View, VaultAppViewModel App) Show(VaultStage stage, double width = 1200)
    {
        var view = (VaultView)VaultDemo.Create(new FakeModule("vault", "Vault"), stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Pump();
        return (window, view, (VaultAppViewModel)view.DataContext!);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static List<TextBlock> Shown(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).ToList();

    private static Button ButtonNamed(Control root, string name) =>
        root.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    private static void Click(Button button)
    {
        // What a tap does: the command, and the Click event for buttons that have a handler instead.
        if (button.Command is { } command && command.CanExecute(button.CommandParameter)) command.Execute(button.CommandParameter);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void Every_screen_of_the_vault_shows_its_own_card()
    {
        var (setup, setupView, setupApp) = Show(VaultStage.Setup);
        Assert.True(setupApp.IsSetup);
        Assert.Single(Shown(setupView, "Create your vault"));
        Assert.Empty(Shown(setupView, "Vault is locked"));
        setup.Close();

        var (locked, lockedView, lockedApp) = Show(VaultStage.Locked);
        Assert.True(lockedApp.IsUnlock);
        Assert.Single(Shown(lockedView, "Vault is locked"));
        Assert.Empty(Shown(lockedView, "Create your vault"));
        locked.Close();

        var (kit, kitView, kitApp) = Show(VaultStage.Kit);
        Assert.True(kitApp.HasKit);
        Assert.Single(Shown(kitView, "Save your Emergency Kit"));
        // The kit takes the page: the items wait until it is confirmed.
        Assert.Empty(Shown(kitView, "Trash"));
        kit.Close();
    }

    [AvaloniaFact]
    public void The_unlocked_vault_lists_the_items_and_opens_the_first_beside_them()
    {
        var (window, view, app) = Show(VaultStage.Items);

        Assert.True(app.IsItems);
        Assert.Equal(9, app.Items.Cards.Count);
        Assert.Single(Shown(view, "Cloudflare"));
        // Wide: the first item is open beside the list, and there is no Back.
        Assert.NotNull(app.Items.Detail);
        Assert.Equal(app.Items.Cards[0].Title, app.Items.Detail!.Title);
        Assert.False(ButtonNamed(view, "Back to the list").IsEffectivelyVisible);
        // A two-factor code shows in the row.
        Assert.Matches(@"^\d{3} \d{3}$", app.Items.Cards.Single(c => c.Title == "GitHub").TotpCode);
        window.Close();
    }

    [AvaloniaFact]
    public void A_section_chip_filters_and_the_trash_shows_what_was_deleted()
    {
        var (window, view, app) = Show(VaultStage.Items);

        Click(ButtonNamed(view, "Logins"));
        Assert.NotEmpty(app.Items.Cards);
        Assert.DoesNotContain(app.Items.Cards, c => !c.IsLogin);
        Click(ButtonNamed(view, "Trash"));
        Assert.Equal(["Old forum account"], app.Items.Cards.Select(c => c.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_shows_the_list_or_the_open_item_never_both()
    {
        var (window, view, app) = Show(VaultStage.Items, width: 390);
        var back = ButtonNamed(view, "Back to the list");

        Assert.Contains("narrow", (IEnumerable<string>)view.Classes);
        Assert.Null(app.Items.Detail); // a phone waits for a tap
        Assert.Single(Shown(view, "Cloudflare"));

        Click(ButtonNamed(view, "Cloudflare"));
        Assert.NotNull(app.Items.Detail);
        Assert.True(back.IsEffectivelyVisible);
        Assert.Empty(Shown(view, "Search the vault")); // the list (and its search) gave way to the item

        Click(back);
        Assert.Null(app.Items.Detail);
        Assert.False(back.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void New_item_opens_the_editor_as_a_sheet_and_cancel_closes_it()
    {
        var (window, view, app) = Show(VaultStage.Items);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<VaultItemView>(), v => ((ItemDetailViewModel)v.DataContext!).IsEditing);

        Click(ButtonNamed(view, "New item"));
        Assert.True(app.Items.IsEditorOpen);
        var editor = Assert.Single(window.GetVisualDescendants().OfType<VaultItemView>(), v => ((ItemDetailViewModel)v.DataContext!).IsEditing);
        Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "New item" && t.IsEffectivelyVisible);

        // Nothing typed: Cancel closes without asking.
        Click(editor.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Cancel"));
        Assert.False(app.Items.IsEditorOpen);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<VaultItemView>(), v => ((ItemDetailViewModel)v.DataContext!).IsEditing);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(860, 390)] // a phone on its side (B3: the new item's fields sat on each other)
    [InlineData(390, 860)]
    public void The_new_items_fields_never_overlap(double width, double height)
    {
        var view = (VaultView)VaultDemo.Create(new FakeModule("vault", "Vault"), VaultStage.Items);
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Pump();
        Click(ButtonNamed(view, "New item"));
        var editor = Assert.Single(window.GetVisualDescendants().OfType<VaultItemView>(), v => ((ItemDetailViewModel)v.DataContext!).IsEditing);

        var fields = editor.GetVisualDescendants().OfType<Control>()
            .Where(c => c is TextBox or ComboBox or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0)
            .Select(c => (Control: c, Box: new Rect(c.TranslatePoint(default, editor) ?? default, c.Bounds.Size)))
            .ToList();
        Assert.NotEmpty(fields);
        foreach (var a in fields)
        foreach (var b in fields)
        {
            if (ReferenceEquals(a.Control, b.Control) || a.Control.GetVisualAncestors().Contains(b.Control) || b.Control.GetVisualAncestors().Contains(a.Control)) continue;
            var overlap = a.Box.Intersect(b.Box);
            Assert.True(overlap.Width < 1 || overlap.Height < 1, $"{Describe(a.Control)} overlaps {Describe(b.Control)} at {width}×{height}");
        }
        window.Close();
        while (Helm.Core.Ui.Sheet.CloseTopmost()) { }
    }

    private static string Describe(Control c) => Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } name ? name : c.GetType().Name;

    [AvaloniaFact]
    public void The_gear_opens_the_settings_as_a_sheet_and_done_closes_it()
    {
        var (window, view, _) = Show(VaultStage.Items);

        Click(ButtonNamed(view, "Vault settings"));
        var settings = Assert.Single(window.GetVisualDescendants().OfType<VaultSettingsView>());
        Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Locking" && t.IsEffectivelyVisible);

        Click(settings.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Done"));
        Assert.Empty(window.GetVisualDescendants().OfType<VaultSettingsView>());
        window.Close();
    }

    [AvaloniaFact]
    public void The_guide_opens_as_a_sheet()
    {
        var (window, view, _) = Show(VaultStage.Locked);

        Click(ButtonNamed(view, "User guide"));
        var guide = Assert.Single(window.GetVisualDescendants().OfType<VaultGuideView>());
        Assert.Contains(guide.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "How the vault works");
        window.Close();
    }
}
