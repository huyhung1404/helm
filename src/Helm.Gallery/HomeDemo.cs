using Avalonia.Controls;
using Helm.Home;
using Helm.Modules.Vault.Ui;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Gallery;

/// <summary>Which Home a Gallery entry shows.</summary>
internal enum HomeStage
{
    /// <summary>A desktop with tools on, shortcuts, an update waiting.</summary>
    Desktop,
    /// <summary>A phone: no shortcuts, no conflicts.</summary>
    Phone,
    /// <summary>Nothing turned on yet.</summary>
    Empty,
    /// <summary>A desktop with a shortcut conflict and an update check running.</summary>
    Attention,
}

/// <summary>The real Home view with a made-up Helm: the tools of today's Helm, a few of them on.</summary>
internal static class HomeDemo
{
    public static Control Create(HomeStage stage)
    {
        var desktop = stage != HomeStage.Phone;
        var viewModel = new HomeViewModel(new NoHost(), "v0.88.0", showsShortcuts: desktop);
        var tools = Tools(stage);
        viewModel.SetTools(tools);
        if (desktop && stage != HomeStage.Empty)
        {
            HomeTool Tool(string id) => tools.Single(t => t.Id == id);
            viewModel.SetShortcuts(
            [
                new HomeShortcut(Tool("quick-capture"), "Capture a thought, a task, a word", ["Win", "Ctrl", "Space"]),
                new HomeShortcut(Tool("window-zones"), "Snap the window to a zone", ["Ctrl", "Alt", "N"]),
                new HomeShortcut(Tool("vault"), "Auto-type a login into the window in front", ["Ctrl", "Alt", "A"]),
                new HomeShortcut(Tool("shortcut-guide"), "Show the shortcuts of the app in front", ["Hold", "Win"]),
            ]);
        }
        switch (stage)
        {
            case HomeStage.Desktop:
            case HomeStage.Phone:
                viewModel.UpdateTitle = "Update ready";
                viewModel.UpdateSubtitle = "v0.88.0 is ready to install";
                viewModel.UpdateTone = HomeTone.Attention;
                break;
            case HomeStage.Attention:
                viewModel.UpdateTitle = "Checking for updates…";
                viewModel.UpdateSubtitle = "Just a moment";
                viewModel.IsCheckingForUpdates = true;
                viewModel.HasConflicts = true;
                viewModel.ConflictSummary = "1 conflict found";
                break;
            default:
                viewModel.UpdateTitle = "You're up to date";
                viewModel.UpdateSubtitle = "Checked 2 hours ago";
                break;
        }
        return new HomeView(viewModel);
    }

    private static List<HomeTool> Tools(HomeStage stage)
    {
        var tracker = new HomeTool("tracker", "Tracker", "Planning", Symbol.TaskListSquare, summary: "3 open · 1 overdue");
        var vault = new HomeTool("vault", "Vault", "Security & Servers", Symbol.ShieldKeyhole, VaultIcon.Image, "Locked");
        var tools = new List<HomeTool>
        {
            new("habits", "Habits", "Planning", Symbol.Grid, summary: "2 of 4 done today"),
            new("missions", "Missions", "Planning", Symbol.Flag, summary: "Aptis B1 · step 3"),
            new("notes", "Notes", "Planning", Symbol.Note),
            new("quick-capture", "Quick Capture", "Planning", Symbol.Flash),
            tracker,
            new("novel-reader", "Novel Reader", "Money & Media", Symbol.Book),
            new("scratch", "Scratch", "Money & Media", Symbol.Edit),
            new("wallet", "Wallet", "Money & Media", Symbol.Payment, summary: "12.4 triệu left this month"),
            new("ssh", "SSH", "Security & Servers", Symbol.Code, summary: "2 servers"),
            vault,
            new("keyboard", "Helm Keyboard", "Windows & Desktop", Symbol.Keyboard, isEnabled: false),
            new("shortcut-guide", "Shortcut Guide", "Windows & Desktop", Symbol.Key),
            new("window-zones", "Window Zones", "Windows & Desktop", Symbol.Grid),
            new("workspace", "Workspace Controller", "Windows & Desktop", Symbol.Link, summary: "1 session running"),
            new("slack", "Slack Bot", "Planning", Symbol.Chat, isEnabled: false),
            new("weekly", "Weekly Report", "Planning", Symbol.Document, isEnabled: false),
            new("vocab", "Vocabulary", "Money & Media", Symbol.Search, summary: "18 to review"),
        };
        if (stage == HomeStage.Empty)
            foreach (var tool in tools) tool.IsEnabled = false;
        // A phone has no Windows tools.
        return stage == HomeStage.Phone ? tools.Where(t => t.Group != "Windows & Desktop").ToList() : tools;
    }

    private sealed class NoHost : IHomeHost
    {
        public Task LaunchAsync(HomeTool tool) => Task.CompletedTask;

        public void OpenWhatsNew() { }

        public void OpenConflicts() { }

        public Task UpdateAsync() => Task.CompletedTask;
    }
}
