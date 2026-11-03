using System.IO;
using System.Text;
using Helm.Ai.Assistant;
using Helm.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Helm.App.Ai;

/// <summary>
/// A conversation's turns as headless <c>grok --prompt-file</c> runs in an empty folder, with none of Grok's own tools,
/// skills, workflows, web search or subagents, and Helm's instructions instead of Grok's coding prompt; later turns
/// resume the session Helm named on the first. What <see cref="GrokChat"/> falls back to when Grok's agent mode cannot
/// be used (about 0.4 s more a turn: a process start and a session reload).
/// </summary>
internal sealed class GrokHeadless(GrokAgentFactory factory, IChildProcessLauncher launcher, ILogger logger, string home, string workFolder,
    string model, string effort)
{
    private string _session = Guid.NewGuid().ToString();
    private bool _started;

    public async Task<(string Answer, AssistantUsage Usage)> TurnAsync(string grok, CliConversation conversation, CancellationToken ct)
    {
        var prompt = Path.Combine(workFolder, _session + ".txt");
        // What waits is only cleared once Grok answered: a failed turn sends it again (a failed first one, with the preamble).
        var sent = conversation.Message(newSession: !_started);
        await File.WriteAllTextAsync(prompt, sent, new UTF8Encoding(false), ct);
        try
        {
            string[] args =
            [
                "--prompt-file", prompt, "--output-format", "json", "--cwd", workFolder,
                "--disallowed-tools", GrokAgentFactory.BuiltInTools, "--disable-web-search", "--no-subagents", "--no-plan", "--no-alt-screen",
                "--system-prompt-override", GrokAgentFactory.SystemPrompt,
                "-m", GrokAgentFactory.Model(model), "--reasoning-effort", GrokAgentFactory.Effort(effort),
                .. _started ? new[] { "-r", _session } : ["-s", _session],
            ];
            ChildProcess process;
            try
            {
                process = launcher.Start(new ChildProcessStartInfo(GrokAgentFactory.CommandLineFor(grok, args), workFolder)
                {
                    Environment = GrokAgentFactory.ChildEnvironment(home),
                });
            }
            catch (ChildProcessException ex)
            {
                throw new AssistantException("Grok could not be started: " + ex.Message, ex);
            }
            using (process)
            using (ct.Register(() => { try { process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }))
            {
                process.StandardInput.Close();
                var errors = ReadAllAsync(process.StandardError);
                var output = await ReadAllAsync(process.StandardOutput);
                await process.Exited;
                ct.ThrowIfCancellationRequested();
                var (answer, error, usage) = GrokHeadlessAnswer.Read(output);
                if (answer is null)
                {
                    error ??= (await errors).Trim() is { Length: > 0 } e ? e : "it stopped without an answer";
                    factory.Failed(error);
                    logger.LogWarning("Grok failed: {Error}", error);
                    // Grok may have kept the session it was asked to start: the next try names a new one.
                    if (!_started) _session = Guid.NewGuid().ToString();
                    throw new AssistantException($"Grok: {error}");
                }
                _started = true;
                // Without usage in the answer: estimated from the text (about 4 characters a token), for what it saved.
                return (answer, usage ?? new AssistantUsage(sent.Length / 4, answer.Length / 4, 0, 0));
            }
        }
        finally
        {
            try
            {
                File.Delete(prompt);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            return await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            return "";
        }
    }
}
