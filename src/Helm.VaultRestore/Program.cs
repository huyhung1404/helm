using System.Text;
using Helm.VaultRestore;

Console.OutputEncoding = Encoding.UTF8;
var tool = new RestoreTool(Console.Out, ReadSecret);
return await tool.RunAsync(args);

// Typed secrets are not echoed; piped input (scripts, tests) is read line by line.
static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var secret = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (secret.Length > 0) secret.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return secret.ToString();
}
