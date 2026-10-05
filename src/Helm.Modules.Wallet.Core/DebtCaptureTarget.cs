using Helm.Core.Capture;
using Helm.Core.Text;

namespace Helm.Modules.Wallet;

/// <summary>A debt as typed in Quick Capture, e.g. "Nam 200k lunch".</summary>
public sealed record DebtCapture(string Person, decimal Amount, DebtEntryKind Kind, string Note);

/// <summary>Reads the short form Quick Capture accepts for the debt book, in English and Vietnamese.</summary>
public static class DebtCaptureParser
{
    /// <summary>
    /// "Nam 200k [note]": Nam owes me. "Nam -200k" or "nợ Nam 200k": I owe Nam. "Nam trả 50k": a repayment.
    /// "Nam nợ 200k": Nam owes me. Null when there is no person or no amount.
    /// </summary>
    public static DebtCapture? Parse(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var sign = token[0] is '-' or '+' ? token[0] : ' ';
            var number = sign == ' ' ? token : token[1..];
            if (!number.Any(char.IsDigit) || !WalletText.TryParseAmount(number, out var amount) || amount <= 0) continue;

            var person = tokens.Take(i).ToList();
            var kind = sign == '-' ? DebtEntryKind.IOwe : DebtEntryKind.OwesMe;
            var lastWord = TextSearch.Fold(person[^1]);
            if (person.Count > 1 && lastWord is "tra" or "paid" or "repaid" or "repay")
            {
                kind = DebtEntryKind.Repayment;
                person.RemoveAt(person.Count - 1);
            }
            else if (person.Count > 1 && lastWord == "no")
            {
                kind = DebtEntryKind.OwesMe;
                person.RemoveAt(person.Count - 1);
            }
            else if (person.Count > 1 && TextSearch.Fold(person[0]) == "no" && sign != '+')
            {
                kind = DebtEntryKind.IOwe;
                person.RemoveAt(0);
            }
            var name = string.Join(' ', person).Trim();
            if (name.Length == 0) return null;
            return new DebtCapture(name, amount, kind, string.Join(' ', tokens.Skip(i + 1)));
        }
        return null;
    }
}

/// <summary>
/// Quick Capture into the debt book: "Nam 200k lunch", "Nam -200k", "Nam trả 50k". The id and prefix are the ones the
/// Tracker's debt book had, so a default target saved in the settings keeps working.
/// </summary>
public sealed class DebtCaptureTarget(DebtBook book) : ICaptureTarget
{
    public const string TargetId = "debt";

    private const string Help = "Type who and how much: Nam 200k (owes you), Nam -200k (you owe), Nam trả 50k (paid back).";

    public string Id => TargetId;
    public string ModuleId => WalletIds.ModuleId;
    public string Name => "Debt";
    public string Prefix => "d";
    public string Example => "Nam 200k lunch · Nam -200k (you owe) · Nam trả 50k (paid back)";
    public int Order => 20;

    public CapturePreview Preview(string text)
    {
        if (DebtCaptureParser.Parse(text) is not { } debt) return new CapturePreview(false, Help);
        var money = WalletFormat.Money(debt.Amount);
        var what = debt.Kind switch
        {
            DebtEntryKind.IOwe => $"You owe {debt.Person} {money}",
            DebtEntryKind.Repayment => book.Balance(debt.Person) switch
            {
                > 0 => $"{debt.Person} pays you back {money}",
                < 0 => $"You pay {debt.Person} back {money}",
                _ => null,
            },
            _ => $"{debt.Person} owes you {money}",
        };
        if (what is null) return new CapturePreview(false, $"{debt.Person} has nothing to repay: the balance is 0.");
        return new CapturePreview(true, debt.Note.Length > 0 ? $"{what} · {debt.Note}" : what);
    }

    public CaptureResult Capture(string text)
    {
        if (DebtCaptureParser.Parse(text) is not { } debt) return new CaptureResult(false, Help);
        try
        {
            book.Add(debt.Person, debt.Amount, debt.Kind, debt.Note);
            return new CaptureResult(true, $"Saved to your debts. {debt.Person}: {DebtFormat.Balance(book.Balance(debt.Person))}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new CaptureResult(false, ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
        }
    }
}
