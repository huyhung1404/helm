using Helm.Core.Settings;
using Helm.Modules.Vocab;

namespace Helm.Tests;

/// <summary>Vocabulary's page view model with everything it needs, for tests.</summary>
internal static class VocabKit
{
    public static VocabSettingsViewModel Settings(ISettingsStoreFactory settings) => new(settings.Get<VocabSettings>(VocabIds.ModuleId));

    public static VocabViewModel ViewModel(VocabStore store, VocabQuiz quiz, ISettingsStoreFactory settings) =>
        new(store, quiz, Settings(settings), new InlineUi(), new AcceptDialogs(), new MemoryClipboard());
}
