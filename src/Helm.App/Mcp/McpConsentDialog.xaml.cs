using System.Windows;
using System.Windows.Threading;
using Helm.Core.Mcp;
using Wpf.Ui.Controls;

namespace Helm.App.Mcp;

/// <summary>Asks whether one MCP tool call may run. <see cref="Answer"/> stays Deny unless an Allow button is clicked.</summary>
internal partial class McpConsentDialog : FluentWindow
{
    /// <summary>How long the Allow buttons stay off after the dialog opens.</summary>
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(800);

    private readonly McpConsentPrompt _prompt;

    public McpConsentDialog(McpConsentPrompt prompt)
    {
        _prompt = prompt;
        InitializeComponent();
        var request = prompt.Request;
        var client = prompt.Client;
        CallerText.Text = client.Version is { } version ? $"{client.Name} {version} asks" : $"{client.Name} asks";
        TitleText.Text = request.Title;
        WhatText.Text = request.WhatItDoes;
        WhyText.Text = request.Elevated ? $"{request.WhyAsk}\n\n{ElevatedLine(request)}" : request.WhyAsk;

        var warnings = new List<string>();
        if (request.Danger == McpDanger.High) warnings.Add("This one can do real harm if it is wrong: read the details before you allow it.");
        if (prompt.CallerNotElevated)
            warnings.Add($"{client.Name} does not run as Administrator, but Helm does: allowing lets it act with Helm's rights.");
        Warning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningText.Text = string.Join("\n", warnings);

        TargetPanel.Visibility = string.IsNullOrWhiteSpace(request.Target) ? Visibility.Collapsed : Visibility.Visible;
        TargetText.Text = request.Target ?? "";
        DetailsPanel.Visibility = string.IsNullOrWhiteSpace(request.Details) ? Visibility.Collapsed : Visibility.Visible;
        DetailsText.Text = request.Details ?? "";
        if (request.Danger == McpDanger.High || (request.Elevated && request.Risk == McpRisk.Remote))
        {
            // The exact command, prominent: this is what the user is agreeing to.
            DetailsLabel.Text = request.Risk == McpRisk.Remote ? "Exactly what will run" : "Exactly what will be sent";
            DetailsBox.BorderBrush = (System.Windows.Media.Brush)FindResource("SystemFillColorCautionBrush");
            DetailsText.FontSize = 13;
        }

        AllowSessionButton.Visibility = prompt.OfferSession ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            DenyButton.Focus();
            var arm = new DispatcherTimer { Interval = ArmDelay };
            arm.Tick += (_, _) =>
            {
                arm.Stop();
                AllowOnceButton.IsEnabled = true;
                AllowSessionButton.IsEnabled = true;
            };
            arm.Start();
        };
    }

    public McpConsentAnswer Answer { get; private set; } = McpConsentAnswer.Deny;

    private static string ElevatedLine(McpConsentRequest request) => request.Risk == McpRisk.Remote
        ? "It runs with full rights on that machine (root or Administrator): one wrong command can change the whole machine."
        : "Helm runs as Administrator, so what Claude does through Helm runs with those rights.";

    private void Deny_Click(object sender, RoutedEventArgs e) => Close();

    private void AllowOnce_Click(object sender, RoutedEventArgs e)
    {
        Answer = McpConsentAnswer.AllowOnce;
        Close();
    }

    private void AllowSession_Click(object sender, RoutedEventArgs e)
    {
        Answer = _prompt.OfferSession ? McpConsentAnswer.AllowForSession : McpConsentAnswer.AllowOnce;
        Close();
    }
}
