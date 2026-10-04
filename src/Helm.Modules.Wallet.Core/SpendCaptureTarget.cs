using Helm.Core.Capture;

namespace Helm.Modules.Wallet;

/// <summary>
/// Quick Capture for Wallet: "/s 50k coffee" saves 50,000 ₫ spent on coffee (cash, or a bank Helm does not read);
/// "/s +2tr bonus" saves money in. A description seen before in one category goes there too.
/// </summary>
public sealed class SpendCaptureTarget(WalletStore store) : ICaptureTarget
{
    public const string TargetId = "spend";

    public string Id => TargetId;
    public string ModuleId => WalletIds.ModuleId;
    public string Name => "Spending";
    public string Prefix => "s";
    public string Example => "50k coffee  ·  +2tr bonus (money in)";
    public int Order => 50;

    /// <summary>The amount (first word; "+" for money in) and the description (the rest).</summary>
    internal static bool TryRead(string text, out decimal amount, out string description)
    {
        amount = 0;
        description = "";
        var s = text.Trim();
        if (s.Length == 0) return false;
        var income = s[0] == '+';
        if (s[0] is '+' or '-') s = s[1..].TrimStart();
        var space = s.IndexOf(' ');
        var first = space < 0 ? s : s[..space];
        if (!WalletText.TryParseAmount(first, out var value)) return false;
        amount = income ? value : -value;
        description = space < 0 ? "" : s[(space + 1)..].Trim();
        return true;
    }

    public CapturePreview Preview(string text)
    {
        if (text.Trim().Length == 0) return new(false, Example);
        if (!TryRead(text, out var amount, out var description)) return new(false, "Start with the amount, e.g. 50k coffee.");
        var category = store.SureCategory(description, amount) is { } id ? store.Category(id)?.Name : null;
        var parts = new List<string> { $"{(amount < 0 ? "Spending" : "Money in")} {WalletFormat.Money(amount)}" };
        if (description.Length > 0) parts.Add(description);
        parts.Add(category ?? "to categorize");
        return new(true, string.Join(" · ", parts));
    }

    public CaptureResult Capture(string text)
    {
        try
        {
            if (!TryRead(text, out var amount, out var description)) return new(false, "Start with the amount, e.g. 50k coffee.");
            var category = store.SureCategory(description, amount);
            store.AddManual(amount, description, store.Now, category);
            return new(true, $"{(amount < 0 ? "Spending" : "Money in")} of {WalletFormat.Money(amount)} saved in Wallet.");
        }
        catch (ArgumentException ex)
        {
            return new(false, ex.Message);
        }
    }
}
