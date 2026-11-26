using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.Ssh;
using Helm.Modules.Ssh.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Gallery;

/// <summary>Which SSH a Gallery entry shows.</summary>
internal enum SshStage
{
    /// <summary>Three servers; the Fairy box connected, its shell in the terminal (PC: import from SSH config).</summary>
    Connected,
    /// <summary>The same on the phone: the key bar under the terminal, no key files.</summary>
    Phone,
    /// <summary>A server that signs in with a password, not connected.</summary>
    Password,
    /// <summary>An AI agent ran something on the connected server: its tab, a run going on.</summary>
    Agent,
    /// <summary>The server's menu.</summary>
    Menu,
    /// <summary>A menu item with parameters: its form.</summary>
    MenuForm,
    /// <summary>A server whose key changed: Helm did not connect.</summary>
    KeyChanged,
    /// <summary>No server yet.</summary>
    FirstRun,
    /// <summary>The settings sheet (the gear).</summary>
    Settings,
    /// <summary>The settings with a server being edited (what AI agents may do there).</summary>
    EditServer,
}

/// <summary>
/// The real SSH view and view model over fake servers: nothing connects anywhere. A connected session is a session object
/// that never opened, marked connected; the terminal is a stand-in that draws a shell's text (the real one is xterm.js
/// in the platform's web view), and the server's menu answers from here.
/// </summary>
internal static class SshDemo
{
    public static Control Create(FakeModule module, SshStage stage)
    {
        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var paths = new HelmPaths(dir);
        var settings = new SettingsStoreFactory(paths);
        var fairy = new SshHost { Name = "Fairy box", Address = "100.64.0.2", User = "fairy", AllowMcp = true, AllowMcpShell = true, MenuPath = "~/.helm/menu" };
        var vps = new SshHost { Name = "WebAdmin VPS", Address = "vps.huyhung1404.com", User = "root", Auth = SshAuthKind.Vault, VaultItemUid = "v1", VaultItemTitle = "VPS root", VaultField = "Private key", AllowMcp = true };
        var pi = new SshHost { Name = "Raspberry Pi", Address = "192.168.1.20", User = "pi", Auth = SshAuthKind.Password };
        if (stage != SshStage.FirstRun)
        {
            settings.Get<SshSettings>(SshIds.ModuleId).Update(s =>
            {
                s.Hosts = [fairy, vps, pi];
                s.KnownHosts =
                [
                    new SshKnownHost("100.64.0.2", 22, "ssh-ed25519", "u8mC1Wq3x0kqT0x3Vh1a9b4mGJ3zO2r7N9yDs6yqv1Y", DateTimeOffset.Now.AddDays(-40)),
                    new SshKnownHost("vps.huyhung1404.com", 22, "ssh-ed25519", "Xk2p9QfB0cRZ3mUq1vT7wE4yH6jL8nA5sD2gK0bM3c8", DateTimeOffset.Now.AddDays(-12)),
                ];
                s.SelectedHostId = stage == SshStage.Password ? pi.Id : fairy.Id;
            });
        }
        var key = new SshDeviceKey(Path.Combine(dir, "ssh-key.bin"), new PlainProtector(), "HUNG-PC");
        key.EnsureCreated();
        var vm = new SshViewModel(settings, key, new AvaloniaUi(), new YesDialogs(), new NoClipboard(), NullLogger<SshViewModel>.Instance);
        var phone = stage == SshStage.Phone;
        var view = new SshView(module, vm, new Host(phone));

        if (stage is SshStage.Connected or SshStage.Phone or SshStage.Agent or SshStage.Menu or SshStage.MenuForm && vm.SelectedHost is { } row)
        {
            // A session that never opened, shown as connected: the page as it is while working on the Fairy box.
            var session = new SshSession(row.Host, 120, 32);
            vm.ActiveSession = session;
            vm.IsConnected = true;
            row.IsConnected = true;
            row.State = "Connected";
            vm.SessionStatus = "Connected · OpenSSH_9.6p1 Ubuntu-3ubuntu13.5";
            vm.Menu.Attach(new DemoMenu(), row.Host.MenuPath, row.Title);
            if (stage == SshStage.Agent)
            {
                var done = session.Agent.Begin("Disk space", "df -h /", new CancellationTokenSource());
                done.Output("Filesystem      Size  Used Avail Use% Mounted on\r\n/dev/sda1        78G   41G   34G  55% /\r\n");
                done.End(new MenuRunResult(0, ""), timedOut: false, timeoutSeconds: 60);
                var running = session.Agent.Begin("Logs of the gateway", "journalctl -u helm-gateway -n 50 -f", new CancellationTokenSource());
                running.Output("Nov 26 14:02:11 fairy helm-gateway[812]: job 4821 claimed by HUNG-PC\r\n");
                running.Output("Nov 26 14:02:13 fairy helm-gateway[812]: job 4821 done in 1.9 s\r\n");
                vm.ShowingAgent = true;
            }
        }
        switch (stage)
        {
            case SshStage.KeyChanged:
                vm.Message = "The key of vps.huyhung1404.com has changed since you trusted it, so Helm did not connect. Someone may be intercepting the "
                             + "connection. If the server was reinstalled, check its new key (SHA256:Q1w2E3r4T5y6U7i8O9p0) on the server, then forget the old "
                             + "one in SSH settings → Known servers and connect again.";
                vm.SelectedHost = vm.Hosts.FirstOrDefault(h => h.Host.Auth == SshAuthKind.Vault);
                break;
            case SshStage.Menu or SshStage.MenuForm:
                view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(async () =>
                {
                    await view.OpenMenuAsync();
                    if (stage == SshStage.MenuForm && vm.Menu.Entries.FirstOrDefault(e => e.HasParams) is { } entry) await vm.Menu.ChooseAsync(entry);
                }, DispatcherPriority.Background);
                break;
            case SshStage.Settings:
                view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => view.OpenSettings(), DispatcherPriority.Background);
                break;
            case SshStage.EditServer:
                view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    vm.EditHostCommand.Execute(vm.Hosts[0]);
                    view.OpenSettings();
                }, DispatcherPriority.Background);
                break;
        }
        return view;
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, string purpose) => data;
        public byte[] Unprotect(byte[] data, string purpose) => data;
    }

    private sealed class Host(bool phone) : ISshPageHost
    {
        public ISshTerminal CreateTerminal(SshViewModel viewModel) => new DemoTerminal(viewModel);
        public string? OpenSshFolder => phone ? null : @"C:\Users\hung\.ssh";
        public bool CanUseKeyFiles => !phone;
        public string KeyProtection => phone
            ? "Its private half is protected by the Android Keystore and never leaves the phone."
            : "Its private half is encrypted for your Windows account and never leaves this PC.";
    }

    /// <summary>The server's menu, as the Fairy box's describes it.</summary>
    private sealed class DemoMenu : IMenuRunner
    {
        private const string Describe = """
            {"protocol": 1, "title": "Fairy box", "groups": [
              {"title": "Status", "items": [
                {"id": "status", "title": "Status", "description": "Uptime, disk, memory and the services.", "kind": "view", "output": "stats"},
                {"id": "logs", "title": "Logs", "description": "The last lines of a service's log, then follows it.", "output": "stream",
                 "params": [{"id": "service", "title": "Service", "type": "choice", "choices": ["helm-gateway", "fairy-core", "nginx", "mongod"]},
                            {"id": "lines", "title": "Lines", "type": "number"}]}
              ]},
              {"title": "Deploy", "items": [
                {"id": "deploy", "title": "Deploy Fairy Core", "description": "Pulls main and restarts fairy-core.", "output": "stream", "danger": "high", "confirm": "Deploy main?"},
                {"id": "restart", "title": "Restart a service", "danger": "confirm",
                 "params": [{"id": "service", "title": "Service", "type": "choice", "choices": ["helm-gateway", "fairy-core", "nginx"]}]}
              ]},
              {"title": "Backups", "items": [
                {"id": "backup", "title": "Back up now", "description": "Encrypted, to the storage box; the monthly restore check stays.", "output": "stream"}
              ]}
            ]}
            """;

        public Task<MenuRunResult> RunStreamingAsync(string command, Action<string>? output, CancellationToken ct)
        {
            if (command.Contains("describe", StringComparison.Ordinal)) output?.Invoke(Describe);
            return Task.FromResult(new MenuRunResult(0, ""));
        }
    }

    /// <summary>
    /// The terminal's stand-in: a shell's text in a terminal's colours (the real one is xterm.js in the platform's web
    /// view), or the AI agent tab's runs.
    /// </summary>
    private sealed class DemoTerminal : ISshTerminal
    {
        private readonly SshViewModel _viewModel;
        private readonly SelectableTextBlock _text = new()
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#D7DCE2")),
        };

        public DemoTerminal(SshViewModel viewModel)
        {
            _viewModel = viewModel;
            View = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#0C0E12")),
                Padding = new Thickness(14, 12),
                Child = new ScrollViewer { Content = _text },
            };
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SshViewModel.ShowingAgent) or nameof(SshViewModel.ActiveSession)) Show();
            };
            Show();
        }

        public Control View { get; }

        public void Focus() => _text.Focus();

        public void SendKey(string key) { }

        public Task PasteAsync() => Task.CompletedTask;

        private void Show()
        {
            var green = new SolidColorBrush(Color.Parse("#7EE787"));
            var blue = new SolidColorBrush(Color.Parse("#79C0FF"));
            var dim = new SolidColorBrush(Color.Parse("#8B949E"));
            var inlines = new InlineCollection();
            void Line(params (string Text, IBrush? Brush)[] parts)
            {
                foreach (var (text, brush) in parts) inlines.Add(brush is null ? new Run(text) : new Run(text) { Foreground = brush });
                inlines.Add(new LineBreak());
            }
            void Prompt(string command) => Line(("fairy@fairy", green), (":", null), ("~/helm-gateway", blue), ("$ " + command, null));
            if (_viewModel.ShowingAgent)
            {
                Line(("── Disk space · df -h / ──", dim));
                Line(("Filesystem      Size  Used Avail Use% Mounted on", null));
                Line(("/dev/sda1        78G   41G   34G  55% /", null));
                Line(("exit 0 · 0.2 s", dim));
                Line(("", null));
                Line(("── Logs of the gateway · journalctl -u helm-gateway -n 50 -f ──", dim));
                Line(("Nov 26 14:02:11 fairy helm-gateway[812]: job 4821 claimed by HUNG-PC", null));
                Line(("Nov 26 14:02:13 fairy helm-gateway[812]: job 4821 done in 1.9 s", null));
                Line(("running…", dim));
            }
            else
            {
                Line(("Welcome to Ubuntu 24.04.3 LTS (GNU/Linux 6.8.0-48-generic x86_64)", null));
                Line(("Last login: Wed Nov 26 09:12:40 2026 from 100.64.0.7", dim));
                Prompt("git pull");
                Line(("Already up to date.", null));
                Prompt("pm2 ls");
                Line(("┌────┬───────────────┬─────────┬──────┬────────┐", dim));
                Line(("│ id │ name          │ status  │ cpu  │ memory │", dim));
                Line(("├────┼───────────────┼─────────┼──────┼────────┤", dim));
                Line(("│ 0  │ helm-gateway  │ ", null), ("online", green), ("  │ 0.3% │ 84 MB  │", null));
                Line(("│ 1  │ fairy-core    │ ", null), ("online", green), ("  │ 1.1% │ 132 MB │", null));
                Line(("└────┴───────────────┴─────────┴──────┴────────┘", dim));
                Prompt("");
            }
            _text.Inlines = inlines;
        }
    }
}
