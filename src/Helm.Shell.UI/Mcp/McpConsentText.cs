using Helm.Core.Mcp;

namespace Helm.Mcp.Ui;

/// <summary>What the consent window says for one tool call: who asks, what happens, why Helm asks, the exact details.</summary>
public sealed class McpConsentText
{
    public McpConsentText(McpConsentPrompt prompt)
    {
        var request = prompt.Request;
        var client = prompt.Client;
        Caller = client.Version is { } version ? $"{client.Name} {version} asks" : $"{client.Name} asks";
        Title = request.Title;
        What = request.WhatItDoes;
        Why = request.Elevated ? $"{request.WhyAsk}\n\n{ElevatedLine(request)}" : request.WhyAsk;

        var warnings = new List<string>();
        if (request.Danger == McpDanger.High) warnings.Add("This one can do real harm if it is wrong: read the details before you allow it.");
        if (prompt.OfferSession && request is { Risk: McpRisk.Remote, Elevated: true })
            warnings.Add($"“Allow for this session” lets {client.Name}'s next ones run with full rights without asking until it disconnects " +
                         "(dangerous ones are still asked).");
        if (prompt.CallerNotElevated)
            warnings.Add($"{client.Name} does not run as Administrator, but Helm does: allowing lets it act with Helm's rights.");
        Warning = string.Join("\n", warnings);

        Target = string.IsNullOrWhiteSpace(request.Target) ? "" : request.Target;
        Details = string.IsNullOrWhiteSpace(request.Details) ? "" : request.Details;
        // The exact command, prominent: this is what the user is agreeing to.
        DetailsProminent = request.Danger == McpDanger.High || (request.Risk == McpRisk.Remote && (request.Elevated || request.Scope is not null));
        DetailsLabel = !DetailsProminent ? "Details" : request.Risk == McpRisk.Remote ? "Exactly what will run" : "Exactly what will be sent";
        OfferSession = prompt.OfferSession;
        // A scoped allowance covers only this exact call (e.g. this command line), not everything the tool can do there.
        SessionText = request.Scope is not null ? "Allow this exact command for this session" : "Allow for this session";
    }

    /// <summary>"Fairy 1.2 asks".</summary>
    public string Caller { get; }

    public string Title { get; }

    public string What { get; }

    public string Why { get; }

    /// <summary>Why to be careful, one line each; empty when nothing needs saying.</summary>
    public string Warning { get; }

    public bool HasWarning => Warning.Length > 0;

    /// <summary>The server, list… the call acts on; empty when none.</summary>
    public string Target { get; }

    public bool HasTarget => Target.Length > 0;

    /// <summary>The exact command line or arguments, shown as they are; empty when none.</summary>
    public string Details { get; }

    public bool HasDetails => Details.Length > 0;

    /// <summary>The details are what the user agrees to (a remote command with full rights, a dangerous call): shown prominently.</summary>
    public bool DetailsProminent { get; }

    public string DetailsLabel { get; }

    /// <summary>"Allow for this session" is offered.</summary>
    public bool OfferSession { get; }

    public string SessionText { get; }

    public string Timeout => "No answer in 2 minutes means no.";

    private static string ElevatedLine(McpConsentRequest request) => request.Risk == McpRisk.Remote
        ? "It runs with full rights on that machine (root or Administrator): one wrong command can change the whole machine."
        : "Helm runs as Administrator, so what an AI agent does through Helm runs with those rights.";
}
