using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helm.Core.Settings;
using Helm.Core.Sync;

namespace Helm.Modules.SlackBot;

/// <summary>Telegram refused or could not be reached.</summary>
public sealed class TelegramException(string message) : Exception(message);

/// <summary>The few Telegram Bot API calls Slack Bot makes (a fake in tests).</summary>
public interface ITelegram
{
    /// <summary>Sends <paramref name="html"/> (Telegram's HTML) to <paramref name="chat"/>.</summary>
    /// <exception cref="TelegramException">Telegram said no or could not be reached.</exception>
    Task SendAsync(string token, long chat, string html, CancellationToken ct);

    /// <summary>The private chat that wrote to the bot last (after /start), or null when nobody did.</summary>
    /// <exception cref="TelegramException">Telegram said no (a wrong token) or could not be reached.</exception>
    Task<(long Id, string Name)?> FindChatAsync(string token, CancellationToken ct);
}

/// <summary>
/// Telegram's Bot API over HTTPS, straight from this PC (no server of the user's in between). The token is the user's
/// own bot's (made with @BotFather).
/// </summary>
public sealed class TelegramApi : ITelegram, IDisposable
{
    private readonly HttpClient _http;

    public TelegramApi(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public void Dispose() => _http.Dispose();

    public async Task SendAsync(string token, long chat, string html, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { chat_id = chat, text = html, parse_mode = "HTML", disable_web_page_preview = true });
        using var json = await CallAsync(token, "sendMessage", body, ct).ConfigureAwait(false);
    }

    public async Task<(long Id, string Name)?> FindChatAsync(string token, CancellationToken ct)
    {
        using var json = await CallAsync(token, "getUpdates", "{}", ct).ConfigureAwait(false);
        (long, string)? found = null;
        foreach (var update in json.RootElement.GetProperty("result").EnumerateArray())
        {
            if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("chat", out var chat)) continue;
            if (chat.TryGetProperty("type", out var type) && type.GetString() != "private") continue;
            var name = string.Join(" ", new[] { "first_name", "last_name" }
                .Select(p => chat.TryGetProperty(p, out var v) ? v.GetString() : null)
                .Where(v => v is { Length: > 0 }));
            found = (chat.GetProperty("id").GetInt64(), name);
        }
        return found;
    }

    private async Task<JsonDocument> CallAsync(string token, string method, string body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await _http.PostAsync($"https://api.telegram.org/bot{token}/{method}", content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TelegramException("Telegram cannot be reached: " + ex.Message);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new TelegramException($"Telegram answered {(int)response.StatusCode}.");
            }
            if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return json;
            var why = json.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            json.Dispose();
            throw new TelegramException("Telegram said: " + (why ?? $"error {(int)response.StatusCode}"));
        }
    }
}

/// <summary>The Telegram bot's token, on this PC only, encrypted for this Windows user (DPAPI); never synced.</summary>
public sealed class SlackTelegramToken(HelmPaths paths, ISecretProtector protector)
{
    private const string Purpose = "helm.slackbot.telegram";

    private string FilePath => Path.Combine(paths.ModuleDataDirectory(SlackBotModule.ModuleId), "telegram.bin");

    public bool Exists => File.Exists(FilePath);

    public string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath), Purpose);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(bytes, Purpose));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    /// <summary>"123456:ABC-…": a bot token's shape, or why not.</summary>
    public static string? Problem(string token) =>
        System.Text.RegularExpressions.Regex.IsMatch(token.Trim(), @"^\d+:[A-Za-z0-9_-]{20,}$")
            ? null
            : "A bot token looks like 123456789:AAH…, from @BotFather's /newbot.";
}
