using Helm.Core.Services;
using Helm.Core.Settings;

namespace Helm.App.Services;

/// <summary>v0.1 placeholder: always reports "up to date" and records the check time in general.json.</summary>
internal sealed class StubUpdateService(ISettingsStoreFactory settings) : IUpdateService
{
    private readonly ISettingsStore<GeneralSettings> _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);

    public string CurrentVersion => AppInfo.Version;

    public DateTimeOffset? LastChecked => _general.Current.LastUpdateCheck;

    public UpdateCheckResult? LastResult { get; private set; }

    public event EventHandler? StateChanged;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        await Task.Delay(400, ct).ConfigureAwait(false);
        LastResult = UpdateCheckResult.UpToDate();
        _general.Update(s => s.LastUpdateCheck = DateTimeOffset.Now);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return LastResult;
    }
}
