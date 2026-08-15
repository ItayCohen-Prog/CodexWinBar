using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CodexWinBar.App.Statistics;

/// <summary>Shared helpers for the statistics inspection surfaces: live regions and count wording.</summary>
internal static class StatisticsAccessibility
{
    /// <summary>A pluralized count for inspection strings ("1 observation", "8 active hours").</summary>
    internal static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? string.Empty : "s")}";

    /// <summary>
    /// Updates a polite live-region line and raises LiveRegionChanged so screen readers announce
    /// keyboard-driven inspection while focus stays on the chart or calendar. WPF only exposes the
    /// LiveSetting metadata; it never raises the change event on its own.
    /// </summary>
    internal static void AnnounceText(TextBlock line, string text)
    {
        if (line.Text == text)
        {
            return;
        }

        line.Text = text;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            var peer = UIElementAutomationPeer.FromElement(line) ?? UIElementAutomationPeer.CreatePeerForElement(line);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
