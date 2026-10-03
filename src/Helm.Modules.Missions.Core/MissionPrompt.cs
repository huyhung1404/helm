using System.Globalization;
using System.Text;

namespace Helm.Modules.Missions;

/// <summary>What the user fills in before copying the prompt; empty fields are left out of it.</summary>
public sealed record MissionPromptInput(string Goal, string Level = "", string Time = "", DateOnly? Deadline = null, string Notes = "");

/// <summary>
/// The prompt the user pastes into any AI (ChatGPT, Gemini, Claude…) to get a mission back as JSON that
/// <see cref="MissionImport"/> reads. It is English, but asks for the content in the language of the goal, so a goal
/// written in Vietnamese gives a Vietnamese mission.
/// </summary>
public static class MissionPrompt
{
    /// <summary>The shape the AI is asked to follow; it always imports (a test checks it).</summary>
    public const string Example = """
        {
          "helmMission": 1,
          "title": "Short name of the mission",
          "goal": "The measurable end result",
          "deadline": "YYYY-MM-DD",
          "reward": "A bigger reward for finishing the whole mission",
          "note": "Optional: anything I should know, e.g. why the deadline is tight",
          "phases": [
            {
              "title": "Phase name",
              "reward": "A small reward for finishing this phase",
              "steps": [
                {
                  "title": "One concrete piece of work",
                  "description": "How to do it",
                  "doneWhen": "How I can check that it is done",
                  "estimateDays": 2,
                  "checklist": ["Part 1", "Part 2"],
                  "resources": ["https://example.com or a book title"]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>The prompt with the user's answers in it.</summary>
    public static string Build(MissionPromptInput input)
    {
        var b = new StringBuilder();
        b.AppendLine("You are an expert coach. Plan a step-by-step roadmap for my goal and return it as JSON that my app, Helm Missions, will import.");
        b.AppendLine();
        b.AppendLine($"My goal: {Line(input.Goal, "<describe your goal>")}");
        if (input.Level.Trim().Length > 0) b.AppendLine($"Where I am now: {Line(input.Level, "")}");
        if (input.Time.Trim().Length > 0) b.AppendLine($"Time I can spend: {Line(input.Time, "")}");
        b.AppendLine(input.Deadline is { } d
            ? $"Deadline: {d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : "Deadline: none, suggest a realistic one");
        if (input.Notes.Trim().Length > 0) b.AppendLine($"Other notes: {Line(input.Notes, "")}");
        b.AppendLine();
        b.AppendLine("Rules:");
        b.AppendLine("- Reply with ONE ```json code block and nothing else.");
        b.AppendLine("- Write every title, description, checklist item and reward in the language of my goal.");
        b.AppendLine("- 3-8 phases in the order I should do them; 3-10 steps per phase.");
        b.AppendLine("- Each step is one concrete piece of work that takes 0.5-3 days at my pace. No vague steps like \"practise more\".");
        b.AppendLine("- \"doneWhen\" says how I can check the step is done: a number, a test, something I produce.");
        b.AppendLine("- \"estimateDays\" must be realistic for my time; the total must fit the deadline. If it cannot, still give the plan and explain in \"note\".");
        b.AppendLine("- \"checklist\" (optional, up to 7 items) splits a step into ticks. \"resources\" (optional) are well-known, real links or book titles; leave it out rather than invent one.");
        b.AppendLine("- End each phase with a short review or test step.");
        b.AppendLine("- Give each phase a small \"reward\" and the whole mission a bigger one.");
        b.AppendLine();
        b.AppendLine("Use exactly this shape:");
        b.AppendLine("```json");
        b.AppendLine(Example);
        b.Append("```");
        return b.ToString();
    }

    /// <summary>
    /// The prompt to plan the rest of the way again: the mission, what is done (with how long it took and the notes),
    /// the pace, what is left, and what changed. The AI answers with only the steps not done yet, in the same format;
    /// <see cref="MissionsStore.Replan"/> puts them in place of the remaining ones.
    /// </summary>
    public static string BuildReplan(Mission mission, IReadOnlyList<MissionStep> steps, MissionPaceInfo pace, DateOnly today, string reason)
    {
        static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var b = new StringBuilder();
        b.AppendLine("You are an expert coach. I am working through a step-by-step roadmap in my app, Helm Missions. Plan the rest of the way again from where I am, and return it as JSON the app will import.");
        b.AppendLine();
        b.AppendLine($"Mission: {Line(mission.Title, "")}");
        if (mission.Goal.Length > 0) b.AppendLine($"Goal: {Line(mission.Goal, "")}");
        b.AppendLine($"Today: {Day(today)}");
        b.AppendLine(mission.Deadline is { } deadline ? $"Deadline: {Day(deadline)}" : "Deadline: none");
        if (mission.StartedAt is not null)
        {
            var off = pace.DaysOff switch { 1 => ", 1 day behind the plan", > 1 => $", {pace.DaysOff} days behind the plan", -1 => ", 1 day ahead of the plan", < 0 => $", {-pace.DaysOff} days ahead of the plan", _ => ", on track" };
            b.AppendLine($"Progress: {pace.Done} of {pace.Total} steps done in {Math.Round(pace.ElapsedDays, 1).ToString(CultureInfo.InvariantCulture)} days{off}.");
        }
        b.AppendLine($"What changed, or what I want: {Line(reason, "nothing in particular; fit the rest to my real pace")}");

        var doneSteps = steps.Where(s => s.IsDone).ToList();
        if (doneSteps.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("Done so far (do not repeat these):");
            foreach (var s in doneSteps)
            {
                var line = $"- {Line(s.Title, "")}";
                if (s.Skipped) line += " (skipped)";
                else if (s.StartedAt is { } st && s.CompletedAt is { } c) line += $" (took {Math.Max(0.1, Math.Round((c - st).TotalDays, 1)).ToString(CultureInfo.InvariantCulture)} days, planned {s.EstimateDays.ToString(CultureInfo.InvariantCulture)})";
                if (s.Note.Length > 0) line += $": {Line(s.Note, "")}";
                b.AppendLine(line);
            }
        }

        b.AppendLine();
        b.AppendLine("Still to do, as planned until now (JSON):");
        b.AppendLine("```json");
        var left = steps.Where(s => !s.IsDone).ToList();
        b.AppendLine(MissionsFormat.Json(mission with { Phases = mission.Phases.Where(p => left.Any(s => s.PhaseKey == p.Key)).ToList() }, left));
        b.AppendLine("```");
        b.AppendLine();
        b.AppendLine("Rules:");
        b.AppendLine("- Reply with ONE ```json code block and nothing else.");
        b.AppendLine("- List ONLY the steps still to do, starting with the one I should do next. Never include the done steps.");
        b.AppendLine("- Keep a phase's exact title to go on with it; give a new phase a new title.");
        b.AppendLine("- Write every title, description, checklist item and reward in the language of my mission.");
        b.AppendLine("- Each step is one concrete piece of work that takes 0.5-3 days at my real pace (see how long the done steps took).");
        b.AppendLine("- \"doneWhen\" says how I can check the step is done: a number, a test, something I produce.");
        b.AppendLine("- If the deadline cannot be met any more, set a realistic new \"deadline\" and explain in \"note\".");
        b.AppendLine("- \"checklist\" (optional, up to 7 items) splits a step into ticks. \"resources\" (optional) are well-known, real links or book titles.");
        b.AppendLine();
        b.AppendLine("Use exactly this shape:");
        b.AppendLine("```json");
        b.AppendLine(Example);
        b.Append("```");
        return b.ToString();
    }

    /// <summary>The template with placeholders, for use outside the form.</summary>
    public static string Template => Build(new MissionPromptInput("<describe your goal>", "<where you are now>", "<e.g. 1 hour a day>", null, "<anything else>"));

    /// <summary>One line: the AI reads the fields as lines of the prompt.</summary>
    private static string Line(string text, string fallback)
    {
        var s = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return s.Length > 0 ? s : fallback;
    }
}
