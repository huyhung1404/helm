using Avalonia.Data.Converters;
using Helm.Modules.Vault.Items;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Modules.Vault;

/// <summary>The icon of a kind of item (the same symbols as on Windows).</summary>
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

/// <summary>Hides a secret field's value behind dots while editing (until revealed).</summary>
public static class SecretChar
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<bool, char>(secret => secret ? '•' : '\0');
}

/// <summary>"#RRGGBB" (an avatar color) to a brush.</summary>
public static class HexBrush
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, Avalonia.Media.IBrush?>(hex =>
        hex is not null && Avalonia.Media.Color.TryParse(hex, out var color) ? new Avalonia.Media.SolidColorBrush(color) : null);
}

/// <summary>An item's own picture (bytes from the sealed item), decoded small; null when absent or unreadable.</summary>
public static class IconBitmap
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<byte[]?, Avalonia.Media.Imaging.Bitmap?>(bytes =>
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 120);
        }
        catch (Exception)
        {
            // A damaged picture shows the letter avatar instead.
            return null;
        }
    });
}
