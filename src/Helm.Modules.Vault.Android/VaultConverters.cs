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
        _ => Symbol.LockClosed,
    });
}

/// <summary>Hides a secret field's value behind dots while editing (until revealed).</summary>
public static class SecretChar
{
    public static IValueConverter Converter { get; } = new FuncValueConverter<bool, char>(secret => secret ? '•' : '\0');
}
