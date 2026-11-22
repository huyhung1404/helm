using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Vault;
using Helm.Modules.Vault.Backup;
using Helm.Modules.Vault.Crypto;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.Session;
using Helm.Modules.Vault.ViewModels;

namespace Helm.Tests;

/// <summary>The vault screens shared by the PC and Android apps, without any UI.</summary>
public sealed class VaultViewModelTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private readonly FakePlatform _platform = new();
    private VaultFiles? _files;
    private SyncedCollection<VaultKeyringData>? _keyrings;

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Setup_creates_the_vault_and_holds_the_emergency_kit_until_confirmed()
    {
        var (app, session, _) = NewApp();
        Assert.Equal(VaultScreen.Setup, app.Screen);

        app.NewPassword = Password;
        app.ConfirmPassword = Password + "x";
        await app.CreateCommand.ExecuteAsync(null);
        Assert.Equal("The two passwords differ.", app.Error);

        app.ConfirmPassword = Password;
        await app.CreateCommand.ExecuteAsync(null);
        Assert.Null(app.Error);
        Assert.Equal(VaultScreen.Items, app.Screen);
        Assert.Equal("", app.NewPassword);
        var kit = Assert.IsType<EmergencyKit>(app.Kit);
        Assert.Contains(kit.RecoveryKey, kit.Text);

        await app.SaveKitCommand.ExecuteAsync(null);
        Assert.Same(kit, _platform.SavedKit);
        app.KitConfirmation = "HELMV-WRONG";
        app.ConfirmKitCommand.Execute(null);
        Assert.NotNull(app.KitError);
        app.KitConfirmation = kit.RecoveryKey;
        app.ConfirmKitCommand.Execute(null);
        Assert.Null(app.Kit);
        Assert.True(session.RecoveryKitConfirmed);
    }

    [Fact]
    public async Task Unlock_shows_errors_and_clears_the_typed_password()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        session.Lock("test");
        Assert.Equal(VaultScreen.Unlock, app.Screen);

        app.Password = "wrong wrong wrong wrong";
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Equal("The vault password is not correct.", app.Error);
        Assert.Equal(VaultScreen.Unlock, app.Screen);

        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Equal(VaultScreen.Items, app.Screen);
        Assert.Equal("", app.Password);
    }

    [Fact]
    public async Task Unlocking_with_nothing_typed_says_so_and_is_not_a_wrong_password()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        session.Lock("test");

        for (var i = 0; i < 3; i++)
        {
            app.Password = "";
            await app.UnlockCommand.ExecuteAsync(null);
            Assert.Equal("Type the vault password.", app.Error); // not "Something went wrong: Argon2 needs a password set"
        }

        // None of them counted: the right password opens at once, with no wait for wrong ones.
        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Null(app.Error);
        Assert.Equal(VaultScreen.Items, app.Screen);

        // Same in the settings: an empty current password is not checked (nor counted).
        var empty = await Assert.ThrowsAsync<VaultKeyException>(() => session.ChangePasswordAsync("", "another good vault password"));
        Assert.Equal("Type the vault password.", empty.Message);
        session.Lock("test");
        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Null(app.Error);
    }

    [Fact]
    public async Task A_password_typed_and_left_on_the_lock_screen_is_forgotten_when_the_page_is_left_or_the_vault_locks()
    {
        var (app, session, _) = NewApp(out var settings);
        await session.CreateAsync(Password);
        session.Lock("test");

        app.Password = "half of my pass";
        app.UseRecoveryKey = true;
        app.RecoveryKey = "HELMV-1234";
        app.ForgetTypedSecrets(); // the page was left or hidden
        Assert.Equal("", app.Password);
        Assert.Equal("", app.RecoveryKey);
        Assert.False(app.UseRecoveryKey);

        // "Forgot it? Use the recovery key" and back: what was typed in the other box goes.
        app.Password = "half of my pass";
        app.ToggleRecoveryKeyCommand.Execute(null);
        Assert.True(app.UseRecoveryKey);
        Assert.Equal("", app.Password);
        app.RecoveryKey = "HELMV-1234";
        app.ToggleRecoveryKeyCommand.Execute(null);
        Assert.False(app.UseRecoveryKey);
        Assert.Equal("", app.RecoveryKey);

        // Typed, then the vault locks (and unlocks with something else): nothing waits in the box.
        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        app.Password = "typed while unlocked";
        session.Lock("inactivity");
        Assert.Equal("", app.Password);

        settings.CurrentPassword = settings.NewPassword = settings.ConfirmPassword = settings.ExportPassword = settings.ExportConfirm = "x";
        settings.ForgetTypedSecrets();
        Assert.All(new[] { settings.CurrentPassword, settings.NewPassword, settings.ConfirmPassword, settings.ExportPassword, settings.ExportConfirm }, v => Assert.Equal("", v));
    }

    [Fact]
    public async Task Secrets_shown_with_the_eye_are_hidden_again_when_the_page_is_left()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;
        items.NewCommand.Execute(VaultItemKind.Login);
        var editor = items.Editor!;
        editor.Title = "Bank";
        editor.Fields.Single(f => f.IsPassword).GenerateCommand.Execute(null);
        editor.SaveCommand.Execute(null);
        var card = Assert.Single(items.Cards);
        card.ToggleRevealCommand.Execute(null);
        var field = items.Detail!.Fields.Single(f => f.IsPassword);
        field.ToggleRevealCommand.Execute(null);
        Assert.True(card.IsRevealed);
        Assert.True(field.IsRevealed);

        items.HideRevealed();
        Assert.False(card.IsRevealed);
        Assert.False(field.IsRevealed);
    }

    [Fact]
    public async Task A_vault_password_too_weak_by_todays_rules_is_flagged_at_unlock_until_it_is_changed()
    {
        var (app, session, _) = NewApp();
        // A vault made before the strength check, with a password it would refuse today.
        const string weak = "Password@2026!!";
        Assert.True(VaultKeyring.IsWeak(weak));
        Assert.False(VaultKeyring.IsWeak(Password));
        var made = VaultKeyring.Create(weak, 0, VaultCryptoTests.CheapKdf(), checkStrength: false);
        made.Key.Dispose();
        _keyrings!.Upsert(VaultKeyringData.RecordId(made.Keyring.VaultId), made.Keyring);
        Assert.Equal(VaultScreen.Unlock, app.Screen);

        app.Password = weak;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Equal(VaultScreen.Items, app.Screen);
        Assert.NotNull(app.WeakPasswordWarning);

        session.Lock("test");
        app.Password = weak;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.NotNull(app.WeakPasswordWarning); // still weak: asked again

        await session.ChangePasswordAsync(weak, Password);
        Assert.Null(app.WeakPasswordWarning);
        session.Lock("test");
        app.Password = Password;
        await app.UnlockCommand.ExecuteAsync(null);
        Assert.Null(app.WeakPasswordWarning);
    }

    [Fact]
    public async Task Items_are_created_in_the_editor_shown_once_saved_searched_and_trashed()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Login);
        Assert.True(items.IsEditorOpen); // adding happens in the editor pop-up
        Assert.Null(items.Detail);
        var editor = items.Editor!;
        Assert.Equal("New item", editor.EditorTitle);
        editor.Title = "Bank";
        editor.Fields.Single(f => f.Kind == VaultFieldKind.Username).Value = "anh@example.com";
        var password = editor.Fields.Single(f => f.IsPassword);
        password.GenerateCommand.Execute(null);
        var generated = password.Value;
        editor.SaveCommand.Execute(null);

        Assert.False(items.IsEditorOpen);
        var card = Assert.Single(items.Cards);
        Assert.Equal("Bank", card.Title);
        Assert.Equal("anh@example.com", card.Subtitle);
        Assert.Same(card, items.SelectedCard); // the new item is shown
        Assert.Equal("Bank", items.Detail!.Title);
        Assert.Equal("••••••••••", items.Detail.Fields.Single(f => f.IsPassword).Display);
        var secret = items.Detail.Fields.Single(f => f.IsPassword);
        secret.ToggleRevealCommand.Execute(null); // the eye next to Copy
        Assert.Equal(generated, secret.Display);

        card.CopyUsernameCommand.Execute(null);
        Assert.Equal("anh@example.com", _platform.CopiedText);
        secret.CopyCommand.Execute(null);
        Assert.Equal(generated, _platform.CopiedSecret);

        // Search finds titles and usernames, never secret values.
        items.Search = "example.com";
        Assert.Single(items.Cards);
        items.Search = generated[..6];
        Assert.Empty(items.Cards);
        Assert.Contains("Nothing matches", items.EmptyText);
        items.Search = "";

        items.OpenCommand.Execute(items.Cards[0]);
        await items.Detail!.MoveToTrashCommand.ExecuteAsync(null);
        Assert.Empty(items.Cards);
        Assert.Null(items.Detail);
        Assert.Equal(1, items.TrashSection.Count);
        items.ShowSectionCommand.Execute(items.TrashSection);
        var trashed = Assert.Single(items.Cards);
        Assert.False(trashed.CanCopySecret); // the trash is for restoring, not copying
        Assert.False(trashed.HasUsername);
    }

    [Fact]
    public async Task A_file_field_holds_its_file_once_saved_and_cancel_leaves_nothing_behind()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Note);
        var editor = items.Editor!;
        editor.Title = "Release keys";
        editor.AddFieldCommand.Execute(VaultFieldKind.Keystore);
        var field = editor.Fields.Last();
        Assert.Equal("Keystore", field.Name);
        Assert.True(field.IsFile);
        Assert.False(field.HasFile);
        Assert.False(field.CanCopy);

        var bytes = new byte[] { 0xFE, 0xED, 0xFE, 0xED, 1, 2, 3 };
        _platform.NextPick = ("helm-release.keystore", "application/octet-stream", bytes);
        await field.ChooseFileCommand.ExecuteAsync(null);
        Assert.True(field.HasFile);
        Assert.StartsWith("helm-release.keystore", field.FileText);
        editor.SaveCommand.Execute(null);

        var saved = Assert.Single(store.Items()).Item;
        var attachment = Assert.Single(saved.Attachments);
        var savedField = saved.Fields.Single(f => f.Kind == VaultFieldKind.Keystore);
        Assert.Equal(attachment.Id, savedField.AttachmentId);

        // Open hands the decrypted file to the platform.
        var shown = items.Detail!.Fields.Single(f => f.IsFile);
        await shown.OpenFileCommand.ExecuteAsync(null);
        Assert.Equal(bytes, _platform.Opened);

        // A file picked and then cancelled never joins the item.
        items.Detail.EditCommand.Execute(null);
        items.Editor!.AddFieldCommand.Execute(VaultFieldKind.Document);
        _platform.NextPick = ("passport.pdf", "application/pdf", [1, 2, 3]);
        await items.Editor.Fields.Last().ChooseFileCommand.ExecuteAsync(null);
        await items.Editor.CancelCommand.ExecuteAsync(null);
        Assert.Null(items.Editor);
        Assert.Single(store.Items().Single().Item.Attachments);

        // Removing the field removes the file from the item (the history still has it).
        items.Detail!.EditCommand.Execute(null);
        items.Editor!.Fields.Single(f => f.IsFile).RemoveCommand.Execute(null);
        items.Editor.SaveCommand.Execute(null);
        Assert.Empty(store.Items().Single().Item.Attachments);
    }

    [Fact]
    public async Task An_image_field_takes_only_pictures_and_shows_a_preview()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        app.Items.NewCommand.Execute(VaultItemKind.Note);
        var editor = app.Items.Editor!;
        editor.AddFieldCommand.Execute(VaultFieldKind.Image);
        var field = editor.Fields.Last();

        _platform.NextPick = ("notes.txt", "text/plain", [65]);
        await field.ChooseFileCommand.ExecuteAsync(null);
        Assert.False(field.HasFile);
        Assert.Contains("not a picture", editor.Error);

        _platform.NextPick = ("card.png", "image/png", [0x89, 0x50, 0x4E, 0x47]);
        await field.ChooseFileCommand.ExecuteAsync(null);
        Assert.True(field.HasFile);
        Assert.True(field.HasPreview);
    }

    [Fact]
    public async Task Documents_attached_before_file_fields_show_as_file_fields()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Passport"));
        await _files!.AttachAsync(uid, "scan.jpg", "image/jpeg", new MemoryStream([1, 2, 3]));

        app.Items.Refresh();
        app.Items.OpenCommand.Execute(app.Items.Cards.Single());
        var field = Assert.Single(app.Items.Detail!.Fields, f => f.IsFile);
        Assert.Equal(VaultFieldKind.Image, field.Kind);
        Assert.True(field.HasFile);
    }

    [Fact]
    public async Task A_token_shows_its_title_and_the_hidden_token_with_copy()
    {
        var (app, session, _) = NewApp();
        await session.CreateAsync(Password);
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Token);
        var editor = items.Editor!;
        var field = Assert.Single(editor.Fields);
        Assert.Equal("Token", field.Name);
        Assert.True(field.IsSecret);
        Assert.Equal(VaultItemKind.Token, editor.Kind);
        editor.Title = "OpenAI API";
        field.Value = "sk-live-abc";
        editor.SaveCommand.Execute(null);

        var card = Assert.Single(items.Cards);
        Assert.True(card.IsToken);
        Assert.False(card.IsLogin); // no letter avatar
        Assert.Equal("Token", card.Subtitle); // never the token itself
        Assert.True(card.HasSecret);
        Assert.DoesNotContain("sk-live", card.SecretDisplay);
        card.CopyCommand.Execute(null);
        Assert.Equal("sk-live-abc", _platform.CopiedSecret);
        Assert.Null(_platform.CopiedText);
    }

    [Fact]
    public async Task An_info_item_shows_its_description_and_a_new_note_starts_empty()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("Cardholder", "NGUYEN"), new VaultField("PIN", "1", VaultFieldKind.Secret)] });
        var items = app.Items;

        items.NewCommand.Execute(VaultItemKind.Note);
        var editor = items.Editor!;
        Assert.Equal(VaultItemKind.Note, editor.Kind);
        Assert.Empty(editor.Fields); // a note has the fields you add
        Assert.Equal("Description", editor.NotesLabel);
        editor.Title = "Wi-Fi";
        editor.Notes = "Router in the hall\nAdmin page 192.168.1.1";
        editor.SaveCommand.Execute(null);

        var wifi = items.Cards.Single(c => c.Title == "Wi-Fi");
        Assert.True(wifi.IsInfo);
        Assert.Equal("Router in the hall", wifi.Subtitle);
        var visa = items.Cards.Single(c => c.Title == "Visa");
        Assert.True(visa.IsInfo);
        Assert.Equal("NGUYEN", visa.Subtitle); // the first plain field, never the PIN
        Assert.False(visa.HasSecret);

        // Editing an older card keeps its own kind (older Helm versions still read it).
        items.OpenCommand.Execute(visa);
        items.Detail!.EditCommand.Execute(null);
        items.Editor!.Notes = "Main card";
        items.Editor.SaveCommand.Execute(null);
        Assert.Equal(VaultItemKind.Card, store.Get(visa.Uid)!.Item.Kind);
    }

    [Fact]
    public async Task A_login_without_a_password_shows_no_secret_line()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Forum") with { Fields = [new VaultField("Username", "anh", VaultFieldKind.Username)] });
        app.Items.Refresh();

        var card = Assert.Single(app.Items.Cards);
        Assert.False(card.HasSecret);
        Assert.False(card.CanCopySecret);
        Assert.Equal("", card.SecretDisplay);
        Assert.True(card.HasUsername);
    }

    [Fact]
    public async Task Sections_list_kinds_two_factor_codes_documents_and_tags_and_search_stays_in_the_section()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)], Tags = ["Money"] });
        store.Add(VaultItem.New(VaultItemKind.Login, "Mail") with { Fields = [new VaultField("Code", "JBSWY3DPEHPK3PXP", VaultFieldKind.Totp)] });
        store.Add(VaultItem.New(VaultItemKind.Token, "GitHub token") with { Fields = [new VaultField("Token", "ghp_x", VaultFieldKind.Secret)] });
        store.Add(VaultItem.New(VaultItemKind.Note, "Wi-Fi") with { Notes = "the bank's guest network" });
        store.Add(VaultItem.New(VaultItemKind.Card, "Visa") with { Fields = [new VaultField("PIN", "1", VaultFieldKind.Password)], Tags = ["money "] });
        store.Add(VaultItem.New(VaultItemKind.Document, "Passport scan"));
        var items = app.Items;
        items.Refresh();

        Assert.Equal(["All items", "Favorites", "Logins", "2FA & tokens", "Cards", "Identities", "Notes", "Documents"], items.Sections.Select(s => s.Label));
        int Count(VaultSectionKind kind) => items.Sections.Single(s => s.Kind == kind).Count;
        Assert.Equal(6, Count(VaultSectionKind.All));
        Assert.Equal(2, Count(VaultSectionKind.Logins));
        Assert.Equal(2, Count(VaultSectionKind.Tokens)); // the token and the login with a two-factor code
        Assert.Equal(1, Count(VaultSectionKind.Cards));
        Assert.Equal(0, Count(VaultSectionKind.Identities));
        Assert.Equal(1, Count(VaultSectionKind.Notes));
        Assert.Equal(1, Count(VaultSectionKind.Documents));
        var money = Assert.Single(items.TagSections); // "Money" and "money " are one tag
        Assert.Equal("Money", money.Label);
        Assert.Equal(2, money.Count);
        Assert.True(items.Sections[0].IsSelected);

        items.ShowSectionCommand.Execute(items.Sections.Single(s => s.Kind == VaultSectionKind.Tokens));
        Assert.Equal(["GitHub token", "Mail"], items.Cards.Select(c => c.Title));
        Assert.True(items.Cards.Single(c => c.Title == "Mail").HasTotp);
        Assert.False(items.Sections[0].IsSelected);

        // Search looks in the section; when nothing is there, it offers every item.
        items.Search = "bank";
        Assert.Empty(items.Cards);
        Assert.True(items.CanSearchEverywhere);
        items.SearchEverywhereCommand.Execute(null);
        Assert.Equal(VaultSectionKind.All, items.Section.Kind);
        Assert.Equal(["Bank", "Wi-Fi"], items.Cards.Select(c => c.Title)); // a note's description is searched
        items.Search = "";

        items.ShowSectionCommand.Execute(money);
        Assert.Equal(["Bank", "Visa"], items.Cards.Select(c => c.Title));

        // The tag goes away with its last item: back to every item.
        foreach (var entry in store.Items().Where(e => e.Item.Tags.Count > 0).ToList())
            store.Save(entry.Uid, entry.Item with { Tags = [] });
        items.Refresh();
        Assert.Empty(items.TagSections);
        Assert.False(items.HasTags);
        Assert.Equal(VaultSectionKind.All, items.Section.Kind);
    }

    [Fact]
    public async Task One_trash_holds_every_kind()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var login = store.Add(VaultItem.New(VaultItemKind.Login, "Old bank"));
        var note = store.Add(VaultItem.New(VaultItemKind.Note, "Old note"));
        store.MoveToTrash(login);
        store.MoveToTrash(note);
        var items = app.Items;
        items.Refresh();

        Assert.Equal(2, items.TrashSection.Count);
        Assert.Equal(0, items.Sections[0].Count);
        items.ShowSectionCommand.Execute(items.TrashSection);
        Assert.True(items.IsTrash);
        Assert.Equal(["Old bank", "Old note"], items.Cards.Select(c => c.Title));

        // Restored: shown where it now lives.
        items.OpenCommand.Execute(items.Cards[0]);
        await items.Detail!.RestoreFromTrashCommand.ExecuteAsync(null);
        Assert.Equal(VaultSectionKind.All, items.Section.Kind);
        Assert.Equal("Old bank", items.SelectedCard!.Title);
    }

    [Fact]
    public async Task Changing_the_kind_moves_an_item_and_keeps_its_fields_and_the_old_version()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Stripe key") with { Fields = [new VaultField("Password", "sk_live_1", VaultFieldKind.Password)] });
        var items = app.Items;
        items.Refresh();
        items.OpenCommand.Execute(items.Cards.Single());
        items.Detail!.EditCommand.Execute(null);
        var editor = items.Editor!;
        Assert.Equal("Edit Stripe key", editor.EditorTitle);
        Assert.Equal(["Login", "Token", "Note", "Card", "Identity", "Document"], editor.KindOptions.Select(o => o.Label));
        editor.KindOption = editor.KindOptions.Single(o => o.Kind == VaultItemKind.Token);
        Assert.Single(editor.Fields); // an existing item keeps its fields
        editor.SaveCommand.Execute(null);

        var saved = store.Get(uid)!.Item;
        Assert.Equal(VaultItemKind.Token, saved.Kind);
        Assert.Equal("sk_live_1", saved.PrimarySecret);
        Assert.Equal(VaultItemKind.Login, Assert.Single(saved.History).Item.Kind);
        items.ShowSectionCommand.Execute(items.Sections.Single(s => s.Kind == VaultSectionKind.Tokens));
        Assert.True(Assert.Single(items.Cards).IsToken);

        // To a note while Tokens is shown: it is no longer there, so it is shown in every item.
        items.OpenCommand.Execute(items.Cards.Single());
        items.Detail!.EditCommand.Execute(null);
        items.Editor!.Kind = VaultItemKind.Note;
        items.Editor.SaveCommand.Execute(null);
        Assert.Equal(VaultItemKind.Note, store.Get(uid)!.Item.Kind);
        Assert.Equal(VaultSectionKind.All, items.Section.Kind);
        Assert.Equal(uid, items.SelectedCard!.Uid);

        // A new item takes the usual fields of the kind picked, until something is typed.
        items.NewCommand.Execute(VaultItemKind.Login);
        items.Editor!.Kind = VaultItemKind.Card;
        Assert.Equal(["Cardholder", "Number", "Expiry", "CVV", "PIN"], items.Editor.Fields.Select(f => f.Name));
        items.Editor.Fields[0].Value = "NGUYEN";
        items.Editor.Kind = VaultItemKind.Identity;
        Assert.Equal("Cardholder", items.Editor.Fields[0].Name); // what was typed stays
    }

    [Fact]
    public async Task An_item_opens_in_the_detail_and_edits_happen_in_the_editor_while_the_detail_stays()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Login, "Bank") with { Fields = [new VaultField("Password", "p", VaultFieldKind.Password)] });
        store.Add(VaultItem.New(VaultItemKind.Login, "Mail"));
        var items = app.Items;
        items.Refresh();
        Assert.Null(items.SelectedCard); // a phone waits for a tap
        var bank = items.Cards.Single(c => c.Title == "Bank");
        var mail = items.Cards.Single(c => c.Title == "Mail");

        items.OpenCommand.Execute(bank);
        Assert.Same(bank, items.SelectedCard);
        Assert.Equal("Bank", items.Detail!.Title);
        items.SelectedCard = mail; // the PC's list selection
        Assert.Equal("Mail", items.Detail!.Title);
        items.CloseDetailCommand.Execute(null); // Back on a phone
        Assert.Null(items.Detail);
        Assert.Null(items.SelectedCard);

        items.OpenCommand.Execute(bank);
        items.Detail!.EditCommand.Execute(null);
        Assert.True(items.IsEditorOpen);
        Assert.False(items.Detail.IsEditing); // the detail keeps showing the item as it is
        items.Editor!.Title = "Bank (typing)";
        Assert.Equal("Bank", items.Detail.Title);
        Assert.True(items.Editor.HasChanges);

        var asked = _platform.Confirms;
        await items.Editor.CancelCommand.ExecuteAsync(null); // something typed: asks first
        Assert.Equal(asked + 1, _platform.Confirms);
        Assert.Null(items.Editor);
        Assert.Equal("Bank", store.Items().Single(e => e.Uid == bank.Uid).Item.Title);

        items.Detail.EditCommand.Execute(null);
        await items.Editor!.CancelCommand.ExecuteAsync(null); // nothing typed: closes at once
        Assert.Equal(asked + 1, _platform.Confirms);

        // The PC selects the first item by itself.
        items.CloseDetailCommand.Execute(null);
        items.AutoSelectFirst = true;
        items.Refresh();
        Assert.Equal("Bank", items.SelectedCard!.Title);
    }

    [Fact]
    public async Task The_star_toggles_favorite_without_a_new_version()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Login, "Bank"));
        var items = app.Items;
        items.Refresh();

        items.ToggleFavoriteCommand.Execute(items.Cards.Single());
        Assert.True(store.Get(uid)!.Item.Favorite);
        Assert.True(items.Cards.Single().Favorite);
        var favorites = items.Sections.Single(s => s.Kind == VaultSectionKind.Favorites);
        Assert.Equal(1, favorites.Count);
        items.ShowSectionCommand.Execute(favorites);
        Assert.Single(items.Cards);

        // The detail's own star.
        items.OpenCommand.Execute(items.Cards.Single());
        items.Detail!.ToggleFavoriteCommand.Execute(null);
        Assert.False(store.Get(uid)!.Item.Favorite);
        Assert.Empty(items.Cards);
        Assert.Empty(store.Get(uid)!.Item.History); // star clicks do not push real edits out of the history
    }

    [Fact]
    public void An_item_kind_from_a_newer_Helm_is_refused_not_guessed()
    {
        // What an older Helm does with a Token (or any kind added later): the item fails to parse, so the store lists
        // it as "cannot be opened" and keeps it, instead of reading it as some other kind and saving that back.
        var future = System.Text.Encoding.UTF8.GetBytes("""{"kind":"somethingNewer","title":"x","fields":[]}""");
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<VaultItem>(future, Helm.Core.Settings.HelmJson.Options));
        var token = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(VaultItem.New(VaultItemKind.Token, "t"), Helm.Core.Settings.HelmJson.Options);
        using var json = System.Text.Json.JsonDocument.Parse(token);
        Assert.Equal("token", json.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Editing_keeps_the_old_version_and_locking_drops_everything_decrypted()
    {
        var (app, session, store) = NewApp();
        await session.CreateAsync(Password);
        var uid = store.Add(VaultItem.New(VaultItemKind.Note, "Wifi") with { Notes = "old" });
        app.Items.Refresh();
        app.Items.OpenCommand.Execute(app.Items.Cards.Single());
        app.Items.Detail!.EditCommand.Execute(null);
        app.Items.Editor!.Notes = "new";
        app.Items.Editor.SaveCommand.Execute(null);
        Assert.Equal("new", store.Get(uid)!.Item.Notes);
        Assert.Single(app.Items.Detail!.History);

        app.Items.Detail.EditCommand.Execute(null);
        app.Items.Editor!.Notes = "typed, then the vault locked";
        session.Lock("test");
        Assert.Empty(app.Items.Cards);
        Assert.Null(app.Items.Detail);
        Assert.Null(app.Items.Editor);
        Assert.Empty(app.Items.TagSections);
        Assert.Equal(VaultScreen.Unlock, app.Screen);
    }

    [Fact]
    public async Task The_vault_says_when_it_locks_and_how_the_backup_is_doing()
    {
        var (app, session, _) = NewApp();
        Assert.Null(app.LockCountdown);
        await session.CreateAsync(Password);
        Assert.Matches(@"^locks in (4:5\d|5:00)$", app.LockCountdown); // 5 minutes by default
        Assert.Equal("Not backed up", app.BackupTitle);
        Assert.False(app.BackupOk);
        Assert.Contains("backup folder", app.BackupDetail);

        session.Lock("test");
        Assert.Null(app.LockCountdown);

        Assert.Equal("OneDrive", VaultBackupService.PlaceName(@"C:\Users\anh\OneDrive\Backup"));
        Assert.Equal("Google Drive", VaultBackupService.PlaceName(@"G:\My Drive\Helm"));
        Assert.Equal("Backup", VaultBackupService.PlaceName("content://com.android.externalstorage.documents/tree/primary%3ABackup"));
        Assert.Equal("Vault", VaultBackupService.PlaceName(@"E:\Vault\"));
        Assert.Null(VaultBackupService.PlaceName(null));
    }

    [Fact]
    public async Task Settings_change_the_password_and_export_to_keepass()
    {
        var (app, session, store) = NewApp(out var settings);
        await session.CreateAsync(Password);
        store.Add(VaultItem.New(VaultItemKind.Note, "Note"));

        settings.CurrentPassword = Password;
        settings.NewPassword = "a brand new vault password 7";
        settings.ConfirmPassword = "a brand new vault password 7";
        await settings.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Null(settings.Error);
        Assert.Equal("", settings.CurrentPassword);

        settings.ExportPassword = settings.ExportConfirm = "a keepass export password";
        await settings.ExportKdbxCommand.ExecuteAsync(null);
        Assert.Null(settings.Error);
        Assert.True(_platform.Created.Length > 100);

        session.Lock("test");
        await settings.BackUpNowCommand.ExecuteAsync(null);
        Assert.Equal("Unlock the vault first (Open vault).", settings.Error);
    }

    private (VaultAppViewModel App, VaultSession Session, VaultStore Store) NewApp() => NewApp(out _);

    private (VaultAppViewModel App, VaultSession Session, VaultStore Store) NewApp(out VaultSettingsViewModel settingsViewModel)
    {
        var paths = new HelmPaths(Path.Combine(_dir, "pc"));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory);
        var engine = Own(new SyncEngine(db, new NullSyncTransport(), new InMemoryMasterKeyStore(), [], debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<VaultItemRecord>(engine, VaultStore.Options));
        var keyrings = _keyrings = Own(new SyncedCollection<VaultKeyringData>(engine, new SyncedCollectionOptions<VaultKeyringData> { Name = VaultSession.KeyringCollection }));
        var settings = Own(new SettingsStoreFactory(paths));
        var session = Own(new VaultSession(keyrings, settings, new NoDeviceUnlock(), engine) { NewKdf = VaultCryptoTests.CheapKdf });
        var store = Own(new VaultStore(records, session));
        var files = _files = new VaultFiles(store, blobs, session);
        var backup = new VaultBackupService(session, store, blobs, new FolderBackupLocation(), settings);
        var app = new VaultAppViewModel(session, store, files, backup, engine, _platform, new ImmediateDispatcher());
        settingsViewModel = new VaultSettingsViewModel(session, store, files, backup, app, new NoDeviceUnlock(), _platform, new ImmediateDispatcher(), settings);
        return (app, session, store);
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : IVaultPlatform
    {
        public string? CopiedSecret { get; private set; }
        public string? CopiedText { get; private set; }
        public EmergencyKit? SavedKit { get; private set; }
        public byte[] Created { get; private set; } = [];

        public (string Name, string MediaType, byte[] Content)? NextPick { get; set; }
        public byte[]? Opened { get; private set; }

        public Task<VaultPickedFile?> PickFileAsync(CancellationToken ct) => Task.FromResult(NextPick is { } pick
            ? new VaultPickedFile(pick.Name, pick.MediaType, new MemoryStream(pick.Content))
            : null);

        public Task<Stream?> CreateFileAsync(string suggestedName, string mediaType, CancellationToken ct) =>
            Task.FromResult<Stream?>(new CapturingStream(bytes => Created = bytes));

        public Task OpenFileAsync(string name, string mediaType, byte[] content, CancellationToken ct)
        {
            Opened = content.ToArray();
            return Task.CompletedTask;
        }
        public void CopySecret(string text) => CopiedSecret = text;
        public void CopyText(string text) => CopiedText = text;
        public int Confirms { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            Confirms++;
            return Task.FromResult(true);
        }
        public Task<string?> PickBackupLocationAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SaveEmergencyKitAsync(EmergencyKit kit, CancellationToken ct)
        {
            SavedKit = kit;
            return Task.CompletedTask;
        }

        public void ShowVault() { }

        private sealed class CapturingStream(Action<byte[]> done) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                done(ToArray());
                base.Dispose(disposing);
            }
        }
    }
}
