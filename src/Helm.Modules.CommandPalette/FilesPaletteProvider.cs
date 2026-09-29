using System.Data.OleDb;
using System.Runtime.Versioning;
using Helm.Core.Palette;
using Helm.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.CommandPalette;

/// <summary>
/// Files and folders from the Windows Search index: the same index the Start menu searches, so it covers the
/// places Windows indexes (the user's folders by default; more under Settings → Search → Searching Windows). Names
/// are matched by word start, every typed word must appear. Opened through Explorer, so they open as the user.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FilesPaletteProvider(ISettingsStoreFactory settings, ILogger<FilesPaletteProvider> logger) : ISlowPaletteProvider
{
    private const int MaxResults = 8;
    private const string Connection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";
    private readonly ISettingsStore<CommandPaletteSettings> _settings = settings.Get<CommandPaletteSettings>(CommandPaletteModule.ModuleId);
    private bool _unavailable;

    public string? ModuleId => null;

    public Task<IReadOnlyList<PaletteItem>> SearchAsync(PaletteQuery query, CancellationToken ct)
    {
        if (!_settings.Current.IncludeFiles || _unavailable || query.Text.Length < 2) return Task.FromResult<IReadOnlyList<PaletteItem>>([]);
        return Task.Run(() => Search(query, ct), ct);
    }

    private IReadOnlyList<PaletteItem> Search(PaletteQuery query, CancellationToken ct)
    {
        if (Sql(query.Text) is not { } sql) return [];
        var results = new List<PaletteItem>();
        try
        {
            using var connection = new OleDbConnection(Connection);
            connection.Open();
            using var command = new OleDbCommand(sql, connection) { CommandTimeout = 3 };
            using var registration = ct.Register(() =>
            {
                try { command.Cancel(); } catch (InvalidOperationException) { }
            });
            using var reader = command.ExecuteReader();
            while (reader.Read() && !ct.IsCancellationRequested)
            {
                if (reader.GetValue(0) is not string path || path.Length == 0) continue;
                // Shortcuts are the apps' own results already.
                if (Path.GetExtension(path) is { } ext && (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))) continue;
                var name = reader.GetValue(1) as string ?? Path.GetFileName(path);
                var isFolder = reader.GetValue(2) is string kind && kind.Equals("Directory", StringComparison.OrdinalIgnoreCase);
                var score = query.Score(name);
                // The index matched it (word starts, any accents): keep it even when our own scoring is stricter.
                if (score <= 0) score = 0.3;
                results.Add(new PaletteItem(name, Path.GetDirectoryName(path) ?? path, isFolder ? PaletteKind.Folder : PaletteKind.File,
                    score, () => Open(path)) { IconFile = path, IsExternal = true });
            }
        }
        catch (OleDbException ex) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "File search cancelled");
        }
        catch (Exception ex) when (ex is OleDbException or InvalidOperationException)
        {
            // No Windows Search service (disabled, or a Windows edition without it): stop asking this session.
            _unavailable = true;
            logger.LogWarning(ex, "Windows Search is not available; the palette will not search files");
        }
        return results;
    }

    /// <summary>
    /// The Windows Search SQL for a query: every word as a word-start match on the file name, folders and files,
    /// most recently changed first. Null when nothing searchable is left after removing quotes and wildcards.
    /// </summary>
    internal static string? Sql(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => c is not ('\'' or '"' or '*' or '%' or '(' or ')')).ToArray()))
            .Where(w => w.Length > 0)
            .Take(6)
            .ToList();
        if (words.Count == 0) return null;
        var contains = string.Join(" AND ", words.Select(w => $"\"{w}*\""));
        return $"SELECT TOP {MaxResults + 4} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType FROM SystemIndex " +
               $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{contains}') ORDER BY System.DateModified DESC";
    }

    private void Open(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Path}", path);
        }
    }
}
