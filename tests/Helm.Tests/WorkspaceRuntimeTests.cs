using Helm.Modules.Workspace;

namespace Helm.Tests;

/// <summary>What the phone sees of a running session: the timeline, the details, the tabs, the history, the modes.</summary>
public class WorkspaceRuntimeTests
{
    [Fact]
    public void The_timeline_has_prompts_thoughts_tools_with_in_and_out_and_answers()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                """{"type":"user","permissionMode":"acceptEdits","origin":{"kind":"human"},"message":{"role":"user","content":"Fix the build"},"timestamp":"2026-10-13T04:00:00Z"}""",
                """{"type":"assistant","thinkingDurationMs":2400,"message":{"model":"claude-opus-5-5","content":[{"type":"thinking","thinking":""}]}}""",
                """{"type":"assistant","thinkingDurationMs":1100,"message":{"model":"claude-opus-5-5","content":[{"type":"thinking","thinking":""}]}}""",
                """{"type":"assistant","effort":"high","message":{"model":"claude-opus-5-5","usage":{"input_tokens":2,"cache_creation_input_tokens":1000,"cache_read_input_tokens":40000},"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet build","description":"Build it"}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Build succeeded.\n0 Warning(s)"}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"C:\\src\\App.cs"}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"a\nb\nc"}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed it."}]}}""",
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>done</task-notification>"}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"Grep","input":{"pattern":"TODO","path":"src"}}]}}""",
            ]);
            var view = TranscriptReader.ReadView(path);
            Assert.Equal("claude-opus-5-5", view.Model);
            Assert.Equal("high", view.Effort);
            Assert.Equal("acceptEdits", view.PermissionMode);
            Assert.Equal(41002, view.ContextTokens);
            Assert.Collection(view.Items,
                i => { Assert.Equal(TranscriptItemKind.User, i.Kind); Assert.Equal("Fix the build", i.Text); },
                // Thinking blocks back to back are one pause.
                i => { Assert.Equal(TranscriptItemKind.Thought, i.Kind); Assert.Equal("Thought for 4s", i.Text); },
                i =>
                {
                    Assert.Equal("Bash", i.Tool);
                    Assert.Equal("Build it", i.Text);
                    Assert.Equal("dotnet build", i.Input);
                    Assert.Equal("Build succeeded.\n0 Warning(s)", i.Output);
                    Assert.False(i.Running);
                },
                i => { Assert.Equal("Read", i.Tool); Assert.Equal(@"C:\src\App.cs", i.Text); Assert.Equal("3 lines", i.Output); },
                i => { Assert.Equal(TranscriptItemKind.Answer, i.Kind); Assert.Equal("Fixed it.", i.Text); },
                // Still running: no result yet.
                i => { Assert.Equal("Grep", i.Tool); Assert.Equal("grep \"TODO\" src", i.Input); Assert.True(i.Running); Assert.Null(i.Output); });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Long_outputs_keep_their_start_and_say_how_much_more()
    {
        var path = Path.GetTempFileName();
        try
        {
            var many = string.Join("\\n", Enumerable.Range(1, 40).Select(i => "line " + i));
            File.WriteAllLines(path,
            [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"seq 40"}}]}}""",
                $$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"{{{many}}}"}]}}""",
            ]);
            var tool = Assert.Single(TranscriptReader.ReadView(path).Items);
            Assert.True(tool.Failed);
            Assert.StartsWith("line 1\nline 2", tool.Output);
            Assert.EndsWith($"… {40 - TranscriptReader.MaxOutputLines} more lines", tool.Output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Running_subagents_are_named_with_their_task_and_latest_step()
    {
        var dir = Directory.CreateTempSubdirectory("helm-transcript-").FullName;
        try
        {
            var path = Path.Combine(dir, "s1.jsonl");
            File.WriteAllLines(path,
            [
                """{"type":"assistant","timestamp":"2026-10-13T04:00:00Z","message":{"content":[{"type":"tool_use","id":"a1","name":"Agent","input":{"subagent_type":"Explore","description":"Find callers"}}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a2","name":"Agent","input":{"subagent_type":"general-purpose","description":"Profile it"}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"a2","content":"done"}]}}""",
            ]);
            var agents = Path.Combine(dir, "s1", "subagents");
            Directory.CreateDirectory(agents);
            File.WriteAllText(Path.Combine(agents, "agent-x.meta.json"), """{"agentType":"Explore","description":"Find every caller","toolUseId":"a1"}""");
            File.WriteAllLines(Path.Combine(agents, "agent-x.jsonl"),
            [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"q","name":"Grep","input":{"pattern":"Build("}}]}}""",
            ]);
            var agent = Assert.Single(TranscriptReader.ReadView(path).Agents);
            Assert.Equal("Explore", agent.Type);
            Assert.Equal("Find every caller", agent.Task);
            Assert.Equal("Grep grep \"Build(\"", agent.Doing);
            Assert.NotNull(agent.Since);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Tabs_are_matched_to_sessions_by_the_name_claude_gives_them()
    {
        var now = DateTimeOffset.Now;
        ClaudeTabMatch.Candidate[] sessions =
        [
            new("a", "Fix the crash when rotating the screen", null, now.AddMinutes(-30)),
            new("b", "Fix the crash when rotating the screen", null, now),
            new("c", "", "bake 300 levels", now.AddMinutes(-5)),
            new("d", "", null, now.AddMinutes(-1)),
            new("e", "Short", null, now),
        ];
        Assert.Equal("Fix the crash when rotat…", ClaudeTabMatch.Label(sessions[0].Title));
        var (open, active) = ClaudeTabMatch.Match(
            [("Fix the crash when rotat…", false), ("Short", true), ("Fix the crash when rotat…", false), ("Claude Code", false), ("bake 300 levels", false), ("Gone", false)],
            sessions);
        // Same name twice: newest first. Untitled: the newest without a title. A prompt names a tab before a title does.
        Assert.Equal(["b", "e", "a", "d", "c", null], open);
        Assert.Equal("e", active);
    }

    [Fact]
    public void Shift_tab_steps_through_the_modes_in_vs_codes_order()
    {
        Assert.Equal(0, PermissionModes.Presses("default", "manual", autoAvailable: true));
        Assert.Equal(2, PermissionModes.Presses(null, "plan", autoAvailable: true));
        Assert.Equal(1, PermissionModes.Presses("auto", "default", autoAvailable: true));
        Assert.Equal(2, PermissionModes.Presses("plan", "acceptEdits", autoAvailable: false));
        Assert.Equal(-1, PermissionModes.Presses("plan", "auto", autoAvailable: false));
        Assert.Equal(-1, PermissionModes.Presses("bypassPermissions", "plan", autoAvailable: true));
        Assert.Equal("Edit automatically", PermissionModes.Label("acceptEdits"));
    }

    [Fact]
    public void History_lists_a_folders_sessions_newest_first_without_empty_ones()
    {
        var root = Directory.CreateTempSubdirectory("helm-projects-").FullName;
        try
        {
            var folder = @"C:\work\app";
            var dir = Directory.CreateDirectory(Path.Combine(root, ClaudeProjects.ProjectKey(folder))).FullName;
            var older = Guid.NewGuid().ToString();
            var newer = Guid.NewGuid().ToString();
            var empty = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(dir, older + ".jsonl"),
            [
                """{"type":"user","message":{"content":"release it"}}""",
                """{"type":"assistant","message":{"model":"claude-opus-5-5","content":[{"type":"text","text":"Released v1."}]}}""",
                """{"type":"ai-title","aiTitle":"Release v1"}""",
            ]);
            File.WriteAllLines(Path.Combine(dir, newer + ".jsonl"), ["""{"type":"user","message":{"content":"untitled question here"}}"""]);
            File.WriteAllLines(Path.Combine(dir, empty + ".jsonl"), ["""{"type":"ai-title","aiTitle":"Nothing"}"""]);
            File.SetLastWriteTimeUtc(Path.Combine(dir, older + ".jsonl"), DateTime.UtcNow.AddHours(-2));

            var history = new ClaudeHistory(root);
            var entries = history.For(folder);
            Assert.Collection(entries,
                e => { Assert.Equal(newer, e.SessionId); Assert.Equal("untitled question here", e.Title); Assert.Equal(1, e.Messages); },
                e =>
                {
                    Assert.Equal("Release v1", e.Title);
                    Assert.Equal("release it", e.FirstPrompt);
                    Assert.Equal("Released v1.", e.LastReply);
                    Assert.Equal("claude-opus-5-5", e.Model);
                    Assert.Equal(2, e.Messages);
                });
            Assert.EndsWith(older + ".jsonl", history.TranscriptOf(folder, older));
            Assert.NotEqual(WorkspaceSync.HistoryId("pc", folder), WorkspaceSync.HistoryId("pc", @"C:\work\other"));
            Assert.Equal(WorkspaceSync.HistoryId("pc", folder), WorkspaceSync.HistoryId("pc", @"c:\work\app\"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hooks_of_042_that_only_heard_the_question_tool_are_installed_again()
    {
        var installed = ClaudeHookConfig.Install(null, 5000, "token");
        Assert.True(ClaudeHookConfig.IsInstalled(installed, 5000, "token"));
        Assert.Contains("SubagentStart", installed);
        var root = System.Text.Json.Nodes.JsonNode.Parse(installed)!;
        root["hooks"]!["PreToolUse"]![0]!["matcher"] = "AskUserQuestion";
        Assert.False(ClaudeHookConfig.IsInstalled(root.ToJsonString(), 5000, "token"));
        root["hooks"]!["PreToolUse"]![0]!["matcher"] = "*";
        Assert.True(ClaudeHookConfig.IsInstalled(root.ToJsonString(), 5000, "token"));
    }

    [Fact]
    public void A_plan_is_held_with_its_text_and_answered_with_a_mode_or_feedback()
    {
        var e = new ClaudeHookEvent
        {
            Event = ClaudeHookEvent.PermissionRequest,
            SessionId = "s",
            ToolName = "ExitPlanMode",
            ToolInputJson = """{"plan":"# Plan\n1. Read\n2. Write"}""",
        };
        var request = PendingRequest.From(e, "r", DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        Assert.Equal(PendingKind.Plan, request.Kind);
        Assert.Equal("# Plan\n1. Read\n2. Write", request.Plan);

        var approve = System.Text.Json.Nodes.JsonNode.Parse(PendingRequest.PlanDecision("acceptEdits", null))!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("allow", approve["behavior"]!.GetValue<string>());
        Assert.Equal("setMode", approve["updatedPermissions"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("acceptEdits", approve["updatedPermissions"]![0]!["mode"]!.GetValue<string>());
        Assert.Equal("session", approve["updatedPermissions"]![0]!["destination"]!.GetValue<string>());

        var keep = System.Text.Json.Nodes.JsonNode.Parse(PendingRequest.PlanDecision(null, "Do step 2 first."))!["hookSpecificOutput"]!["decision"]!;
        Assert.Equal("deny", keep["behavior"]!.GetValue<string>());
        Assert.Equal("Keep planning. Do step 2 first.", keep["message"]!.GetValue<string>());
    }

    [Fact]
    public void Claude_codes_model_list_comes_from_its_initialize_answer()
    {
        var line = """{"type":"control_response","response":{"subtype":"success","response":{"commands":[],"models":[{"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","description":"Opus 5.5 · Best","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"],"supportsAutoMode":true},{"value":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5","description":"Fastest"}]}}}""";
        var (models, auto) = ClaudeCatalog.ParseModels(line);
        Assert.True(auto);
        Assert.Collection(models,
            m => { Assert.Equal("default", m.Value); Assert.Equal("claude-opus-5-5", m.ResolvedModel); Assert.Equal(5, m.EffortLevels.Count); },
            m => { Assert.Equal("Haiku 4.5", m.DisplayName); Assert.Empty(m.EffortLevels); Assert.Equal("claude-haiku-4-5-20251001", m.ResolvedModel); });
        Assert.Empty(ClaudeCatalog.ParseModels("not json").Models);
    }
}
