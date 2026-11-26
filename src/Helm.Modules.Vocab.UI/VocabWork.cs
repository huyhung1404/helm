namespace Helm.Modules.Vocab.Ui;

/// <summary>What the AI agent has to do besides breaking the waiting words down (those show as chips of their own).</summary>
internal static class VocabWork
{
    public static IReadOnlyList<string> Others(VocabViewModel vm)
    {
        var lines = new List<string>();
        if (vm.TextCount > 0) lines.Add(vm.TextCount == 1 ? "1 text to pick words from" : $"{vm.TextCount} texts to pick words from");
        if (vm.HardCount > 0)
            lines.Add(vm.HardCount == 1 ? "a new way to remember 1 word you keep forgetting" : $"new ways to remember {vm.HardCount} words you keep forgetting");
        if (vm.SlipCount > 0) lines.Add(vm.SlipCount == 1 ? "a pinyin hook for 1 Chinese word" : $"pinyin hooks for {vm.SlipCount} Chinese words");
        if (vm.LackingCount > 0)
            lines.Add(vm.LackingCount == 1 ? "word forms and synonyms for 1 word" : $"word forms and synonyms for {vm.LackingCount} words");
        return lines;
    }
}
