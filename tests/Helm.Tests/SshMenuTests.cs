using System.Diagnostics;
using Helm.Modules.Ssh;

namespace Helm.Tests;

public sealed class SshMenuTests
{
    private const string Description = """
        {
          "protocol": 1,
          "title": "WebAdmin",
          "groups": [
            { "title": "Deploy", "items": [
              { "id": "deploy", "title": "Deploy", "kind": "action", "output": "stream", "danger": "high", "confirm": "Deploy main to production?" },
              { "id": "restart", "title": "Restart app", "output": "text", "danger": "confirm",
                "params": [ { "id": "app", "title": "App", "type": "choice", "choices": "dynamic" } ] },
              { "id": "logs", "title": "Logs", "output": "text",
                "params": [ { "id": "lines", "title": "Lines", "type": "number", "min": 1, "max": 500, "default": "50" },
                            { "id": "grep", "title": "Filter", "type": "text", "pattern": "[a-z ]*", "maxLength": 20, "required": false } ] }
            ]},
            { "title": "Status", "items": [
              { "id": "pm2", "title": "Processes", "kind": "view", "output": "table", "refresh": 10, "rowActions": ["restart"] },
              { "id": "health", "title": "Server", "kind": "view", "output": "stats" }
            ]}
          ]
        }
        """;

    private const string Table = """
        { "columns": [ { "id": "name", "title": "Name" }, { "id": "status", "title": "Status" }, { "id": "cpu", "title": "CPU" } ],
          "rows": [ { "key": "backend", "cells": { "name": "backend", "status": "online", "cpu": 3, "extra": "dropped" } },
                    { "key": "web", "cells": { "name": "web", "status": "stopped" } } ] }
        """;

    // ---- The contract ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_menu_description_is_read()
    {
        var menu = SshMenu.ParseDescription(Description);
        Assert.Equal("WebAdmin", menu.Title);
        Assert.Equal(["Deploy", "Status"], menu.Groups.Select(g => g.Title));
        var deploy = menu.Find("deploy")!;
        Assert.Equal((MenuItemKind.Action, MenuOutput.Stream, MenuDanger.High, "Deploy main to production?"), (deploy.Kind, deploy.Output, deploy.Danger, deploy.Confirm));
        var app = Assert.Single(menu.Find("restart")!.Params);
        Assert.Equal((MenuParamType.Choice, true, true), (app.Type, app.DynamicChoices, app.Required));
        var logs = menu.Find("logs")!;
        Assert.Equal((MenuParamType.Number, 1.0, 500.0, "50"), (logs.Params[0].Type, logs.Params[0].Min, logs.Params[0].Max, logs.Params[0].Default));
        Assert.Equal((false, 20), (logs.Params[1].Required, logs.Params[1].MaxLength));
        var pm2 = menu.Find("pm2")!;
        Assert.Equal((MenuItemKind.View, MenuOutput.Table, 10, "restart"), (pm2.Kind, pm2.Output, pm2.Refresh, pm2.RowActions.Single()));
        Assert.Equal(MenuOutput.Stats, menu.Find("health")!.Output);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "protocol": 2, "groups": [] }""")]
    [InlineData("""{ "groups": [] }""")]
    [InlineData("""{ "protocol": 1 }""")]
    [InlineData("""{ "protocol": 1, "groups": [ { "items": [ { "id": "Bad Id" } ] } ] }""")]
    [InlineData("""{ "protocol": 1, "groups": [ { "items": [ { "id": "a" }, { "id": "a" } ] } ] }""")]
    [InlineData("""{ "protocol": 1, "groups": [ { "items": [ { "id": "a", "rowActions": ["nope"] } ] } ] }""")]
    [InlineData("""{ "protocol": 1, "groups": [ { "items": [ { "id": "a", "params": [ { "id": "x", "type": "choice" } ] } ] } ] }""")]
    [InlineData("""{ "protocol": 1, "groups": [ { "items": [ { "id": "a", "params": [ { "id": "x" }, { "id": "x" } ] } ] } ] }""")]
    public void Broken_menus_are_refused_with_a_reason(string json)
    {
        var ex = Assert.Throws<MenuFormatException>(() => SshMenu.ParseDescription(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Oversized_menus_are_refused()
    {
        var items = string.Join(",", Enumerable.Range(0, 201).Select(i => $$"""{ "id": "i{{i}}" }"""));
        Assert.Throws<MenuFormatException>(() => SshMenu.ParseDescription($$"""{ "protocol": 1, "groups": [ { "items": [ {{items}} ] } ] }"""));
        var deep = new string('[', 100) + new string(']', 100);
        Assert.Throws<MenuFormatException>(() => SshMenu.ParseChoices(deep));
    }

    [Fact]
    public void Tables_stats_and_choices_are_read()
    {
        var table = SshMenu.ParseTable(Table);
        Assert.Equal(["name", "status", "cpu"], table.Columns.Select(c => c.Id));
        Assert.Equal("3", table.Rows[0].Cells["cpu"]);
        Assert.False(table.Rows[0].Cells.ContainsKey("extra"));
        Assert.Equal("web", table.Rows[1].Key);

        var stats = SshMenu.ParseStats("""{ "stats": [ { "label": "Disk", "value": "78%", "level": "warn" }, { "label": "Up", "value": 12 } ] }""");
        Assert.Equal([new MenuStat("Disk", "78%", "warn"), new MenuStat("Up", "12", "")], stats);

        Assert.Equal(["backend", "web"], SshMenu.ParseChoices("""["backend", "web"]"""));
        Assert.Throws<MenuFormatException>(() => SshMenu.ParseChoices("""{ "a": 1 }"""));
    }

    [Fact]
    public void Row_actions_take_their_values_from_the_row()
    {
        var menu = SshMenu.ParseDescription(Description);
        var row = SshMenu.ParseTable(Table).Rows[0];
        Assert.Equal(new Dictionary<string, string> { ["app"] = "backend" }, SshMenu.RowValues(menu.Find("restart")!, row));
    }

    // ---- What reaches the server -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("~/.helm/menu", true)]
    [InlineData("/opt/helm/menu.sh", true)]
    [InlineData("menu", true)]
    [InlineData("~/a b", false)]
    [InlineData("~/x;rm -rf ~", false)]
    [InlineData("$(id)", false)]
    [InlineData("`id`", false)]
    [InlineData("~/../etc/passwd", false)]
    [InlineData("~root/menu", false)]
    [InlineData("", false)]
    public void Only_plain_menu_paths_are_used(string path, bool ok) => Assert.Equal(ok, SshMenu.IsValidPath(path));

    [Fact]
    public void Arguments_are_quoted_one_by_one()
    {
        var menu = SshMenu.ParseDescription(Description);
        var args = SshMenu.RunArguments(menu.Find("logs")!, new Dictionary<string, string> { ["lines"] = "50", ["grep"] = "it's" });
        Assert.Equal(["run", "logs", "--lines=50", "--grep=it's"], args);
        Assert.Equal("~/.helm/menu 'run' 'logs' '--lines=50' '--grep=it'\\''s'", SshMenu.Command("~/.helm/menu", args));
        Assert.Throws<ArgumentException>(() => SshMenu.Command("~/x;id", "describe"));
    }

    [SshShellFact]
    public void Quoted_arguments_reach_the_program_unchanged_whatever_they_hold()
    {
        string[] nasty = ["plain", "it's", "a b", "$(id)", "`id`", "; rm -rf ~", "\"quoted\"", "back\\slash", "*", "'", "''", "new\nline", "--x=y", "tiếng Việt"];
        var start = new ProcessStartInfo(SshShellFactAttribute.Bash!) { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("-c");
        // printf stands in for the menu: one line per argument, NUL-separated so line breaks survive.
        start.ArgumentList.Add("printf '%s\\0' " + string.Join(' ', nasty.Select(SshMenu.Quote)));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(nasty, output.Split('\0')[..^1]);
    }

    [Theory]
    [InlineData("app", "backend", null)]
    [InlineData("app", "nope", "list")]
    [InlineData("app", "", "needed")]
    [InlineData("lines", "50", null)]
    [InlineData("lines", "0", "at least")]
    [InlineData("lines", "501", "at most")]
    [InlineData("lines", "5e1", null)]
    [InlineData("lines", "ten", "number")]
    [InlineData("lines", "NaN", "number")]
    [InlineData("grep", "", null)]
    [InlineData("grep", "error", null)]
    [InlineData("grep", "error;rm", "form")]
    [InlineData("grep", "this is far too long a filter", "characters")]
    [InlineData("grep", "a\u0000b", "control")]
    public void Values_are_checked_against_what_the_menu_declared(string paramId, string value, string? error)
    {
        var menu = SshMenu.ParseDescription(Description);
        var param = menu.Groups.SelectMany(g => g.Items).SelectMany(i => i.Params).First(p => p.Id == paramId);
        var result = SshMenu.Validate(param, value, ["backend", "web"]);
        if (error is null) Assert.Null(result);
        else Assert.Contains(error, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Yes_no_values_are_true_or_false()
    {
        var flag = new MenuParam("force", "Force", MenuParamType.Bool, [], false, null, 200, null, null, null, true);
        Assert.Null(SshMenu.Validate(flag, "true", []));
        Assert.NotNull(SshMenu.Validate(flag, "yes", []));
    }

    // ---- The popup's view model ------------------------------------------------------------------------------------

    [Fact]
    public async Task Choosing_an_item_types_its_command_into_the_terminal()
    {
        var server = new FakeMenu();
        var typed = new List<string>();
        var asked = new List<string>();
        var vm = new SshMenuViewModel((_, message, _) => { asked.Add(message); return Task.FromResult(true); }, typed.Add);
        var closed = 0;
        vm.Typed += (_, _) => closed++;
        Assert.False(vm.CanOpen);
        vm.Attach(server, null, "vps");
        Assert.True(vm.CanOpen);
        await vm.OpenAsync();
        Assert.Equal(MenuState.Ready, vm.State);
        Assert.Equal("WebAdmin", vm.Title);
        Assert.Equal(["deploy", "restart", "logs", "pm2", "health"], vm.Entries.Select(e => e.Item.Id));
        Assert.Equal([true, false, false, true, false], vm.Entries.Select(e => e.StartsGroup));
        Assert.True(vm.IsListOpen);

        // No parameters, not dangerous: typed at once, with a leading space (kept out of history) and Enter.
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "pm2"));
        Assert.Equal(" ~/.helm/menu 'run' 'pm2'\r", typed.Single());
        Assert.Empty(asked);
        Assert.Equal(1, closed);

        // Dangerous: the question shows the exact line first.
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "deploy"));
        Assert.Contains("Deploy main to production?", asked.Single(), StringComparison.Ordinal);
        Assert.Contains("~/.helm/menu 'run' 'deploy'", asked.Single(), StringComparison.Ordinal);
        Assert.Equal(" ~/.helm/menu 'run' 'deploy'\r", typed[^1]);
        Assert.Equal(2, closed);

        // The menu is read once; opening again does not ask the server.
        await vm.OpenAsync();
        Assert.Single(server.Commands, c => c.EndsWith("'describe'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_with_parameters_opens_its_form_with_the_servers_choices()
    {
        var server = new FakeMenu();
        var typed = new List<string>();
        var vm = new SshMenuViewModel((_, _, _) => Task.FromResult(true), typed.Add);
        vm.Attach(server, null, "vps");
        await vm.OpenAsync();
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "restart"));
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.IsListOpen);
        await Until(() => vm.Fields.Single().Choices.Count == 2);
        var app = vm.Fields.Single();
        Assert.Equal(["backend", "web"], app.Choices);

        // Nothing chosen, or something not offered: refused, nothing typed.
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("needed", app.Error, StringComparison.Ordinal);
        app.Value = "web; rm -rf ~";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Contains("list", app.Error, StringComparison.Ordinal);
        Assert.Empty(typed);

        app.Value = "web";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(" ~/.helm/menu 'run' 'restart' '--app=web'\r", typed.Single());
        Assert.False(vm.IsFormOpen);

        // Back returns to the list; values are quoted whatever they hold.
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "logs"));
        Assert.Equal("50", vm.Fields[0].Value);
        vm.BackCommand.Execute(null);
        Assert.True(vm.IsListOpen);
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "logs"));
        vm.Fields[1].Value = "it s";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(" ~/.helm/menu 'run' 'logs' '--lines=50' '--grep=it s'\r", typed[^1]);
    }

    [Fact]
    public async Task Declining_a_dangerous_item_types_nothing()
    {
        var typed = new List<string>();
        var vm = new SshMenuViewModel((_, _, _) => Task.FromResult(false), typed.Add);
        var closed = 0;
        vm.Typed += (_, _) => closed++;
        vm.Attach(new FakeMenu(), null, "vps");
        await vm.OpenAsync();
        await vm.ChooseAsync(vm.Entries.Single(e => e.Item.Id == "deploy"));
        Assert.Empty(typed);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task A_missing_or_broken_menu_is_explained_and_read_again_on_the_next_open()
    {
        var server = new FakeMenu { DescribeExit = 127 };
        var vm = new SshMenuViewModel((_, _, _) => Task.FromResult(true), _ => { });
        vm.Attach(server, "~/bin/menu", "vps");
        await vm.OpenAsync();
        Assert.Equal(MenuState.Missing, vm.State);
        Assert.Contains("~/bin/menu", vm.Problem, StringComparison.Ordinal);
        Assert.False(vm.IsListOpen);

        server.DescribeExit = 0;
        server.DescribeJson = """{ "protocol": 9, "groups": [] }""";
        await vm.OpenAsync();
        Assert.Equal(MenuState.Failed, vm.State);
        Assert.Contains("protocol", vm.Problem, StringComparison.Ordinal);

        server.DescribeJson = Description;
        await vm.OpenAsync();
        Assert.Equal(MenuState.Ready, vm.State);
        Assert.False(vm.HasProblem);
    }

    [Fact]
    public async Task Switching_servers_reads_the_other_menu()
    {
        var vm = new SshMenuViewModel((_, _, _) => Task.FromResult(true), _ => { });
        vm.Attach(new FakeMenu(), null, "a");
        await vm.OpenAsync();
        Assert.Equal("WebAdmin", vm.Title);
        vm.Attach(new FakeMenu { DescribeJson = """{ "protocol": 1, "title": "Other", "groups": [] }""" }, null, "b");
        Assert.Equal(MenuState.Idle, vm.State);
        await vm.OpenAsync();
        Assert.Equal(("Other", 0), (vm.Title, vm.Entries.Count));
        vm.Attach(null, null, "");
        Assert.False(vm.CanOpen);
    }

    /// <summary>Waits for the view model to finish what the fake server answered (its continuations run on the pool).</summary>
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "The view model did not get there in time.");
    }

    /// <summary>A server whose menu answers like the WebAdmin one.</summary>
    private sealed class FakeMenu : IMenuRunner
    {
        private readonly List<Task> _running = [];

        public List<string> Commands { get; } = [];

        public string DescribeJson { get; set; } = Description;

        public int DescribeExit { get; set; }

        public bool FailDeploy { get; set; }

        public async Task Idle()
        {
            Task[] running;
            lock (_running) running = [.. _running];
            await Task.WhenAll(running);
        }

        public Task<MenuRunResult> RunStreamingAsync(string command, Action<string>? output, CancellationToken ct)
        {
            var task = Task.Run(() => Answer(command, output));
            lock (_running) _running.Add(task);
            return task;
        }

        private MenuRunResult Answer(string command, Action<string>? output)
        {
            lock (Commands) Commands.Add(command);
            if (command.StartsWith("~/bin/menu ", StringComparison.Ordinal) && !command.EndsWith("'describe'", StringComparison.Ordinal))
                command = "~/.helm/menu" + command["~/bin/menu".Length..];
            switch (command)
            {
                case "~/.helm/menu 'describe'" or "~/bin/menu 'describe'":
                    if (DescribeExit != 0) return new MenuRunResult(DescribeExit, "bash: ~/bin/menu: No such file or directory");
                    output?.Invoke(DescribeJson);
                    return new MenuRunResult(0, "");
                case "~/.helm/menu 'choices' 'restart' 'app'":
                    output?.Invoke("""["backend","web"]""");
                    return new MenuRunResult(0, "");
                case "~/.helm/menu 'run' 'pm2'":
                    output?.Invoke(Table);
                    return new MenuRunResult(0, "");
                case "~/.helm/menu 'run' 'deploy'":
                    output?.Invoke("=== Pulling ===\n");
                    output?.Invoke(FailDeploy ? "npm ERR! build failed\n" : "=== Done ===\n");
                    return new MenuRunResult(FailDeploy ? 2 : 0, "");
                default:
                    if (command.StartsWith("~/.helm/menu 'run' 'restart' '--app=", StringComparison.Ordinal))
                    {
                        output?.Invoke($"Restarted {command.Split("--app=")[1].TrimEnd('\'')}.\n");
                        return new MenuRunResult(0, "");
                    }
                    return new MenuRunResult(1, "unknown command");
            }
        }
    }
}
