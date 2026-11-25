using Avalonia.Data.Converters;
using Helm.Modules.Vault.Items;
using Helm.Modules.Vault.ViewModels;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Modules.Vault.Ui;

/// <summary>The icon of a kind of item.</summary>
public static class KindIcon
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<VaultItemKind, Symbol>(kind => kind switch
    {
        VaultItemKind.Login => Symbol.Key,
        VaultItemKind.Note => Symbol.Note,
        VaultItemKind.Card => Symbol.Payment,
        VaultItemKind.Identity => Symbol.PersonKey,
        VaultItemKind.Document => Symbol.Document,
        VaultItemKind.Token => Symbol.Code,
        _ => Symbol.LockClosed,
    });
}

/// <summary>The icon of a section of the vault (the chip row).</summary>
public static class SectionIcon
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<VaultSectionKind, Symbol>(kind => kind switch
    {
        VaultSectionKind.All => Symbol.Grid,
        VaultSectionKind.Favorites => Symbol.Star,
        VaultSectionKind.Logins => Symbol.Key,
        VaultSectionKind.Tokens => Symbol.ShieldKeyhole,
        VaultSectionKind.Cards => Symbol.Payment,
        VaultSectionKind.Identities => Symbol.PersonKey,
        VaultSectionKind.Notes => Symbol.Note,
        VaultSectionKind.Documents => Symbol.Attach,
        VaultSectionKind.Tag => Symbol.Tag,
        VaultSectionKind.Trash => Symbol.Delete,
        _ => Symbol.Grid,
    });
}

/// <summary>Hides a secret field's value behind dots while editing (until revealed).</summary>
public static class SecretChar
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<bool, char>(secret => secret ? '•' : '\0');
}

/// <summary>"#RRGGBB" (an avatar colour) to a brush.</summary>
public static class HexBrush
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, Avalonia.Media.IBrush?>(hex =>
        hex is not null && Avalonia.Media.Color.TryParse(hex, out var color) ? new Avalonia.Media.SolidColorBrush(color) : null);
}

/// <summary>An item's own picture (bytes from the sealed item), decoded small; null when absent or unreadable.</summary>
public static class IconBitmap
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<byte[]?, Avalonia.Media.Imaging.Bitmap?>(bytes => Decode(bytes, 120));

    internal static Avalonia.Media.Imaging.Bitmap? Decode(byte[]? bytes, int width)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            // A damaged picture shows the letter avatar instead.
            return null;
        }
    }
}

/// <summary>An image field's picture for the item view, decoded to a preview width; null when unreadable.</summary>
public static class PreviewBitmap
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<byte[]?, Avalonia.Media.Imaging.Bitmap?>(bytes => IconBitmap.Decode(bytes, 480));
}
