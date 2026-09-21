using System.Windows.Controls;

namespace DevBar.Sdk;

/// <summary>
/// Contract for a DevBar module. Implement this, drop the compiled DLL in
/// %LOCALAPPDATA%\DevBar\modules\ and DevBar picks it up on next start.
///
/// The lifecycle is the performance contract: a module must do NOTHING
/// (no timers, no polling, no I/O) between OnCollapsed and the next
/// OnExpanded. DevBar stays invisible most of the day - modules that
/// poll in the background get the whole app uninstalled.
/// </summary>
public interface IDevBarModule
{
    /// <summary>Stable unique id, e.g. "clipboard". Used in config.</summary>
    string Id { get; }

    /// <summary>Shown in the bar header when the module is in focus.</summary>
    string DisplayName { get; }

    /// <summary>A Segoe Fluent Icons glyph, e.g. "".</summary>
    string IconGlyph { get; }

    /// <summary>
    /// Build the card shown when this module is paged into view.
    /// Called once, lazily, on first view - cache your control.
    /// </summary>
    UserControl BuildCard();

    /// <summary>Module paged into view while the bar is expanded. Start timers, refresh data.</summary>
    void OnExpanded();

    /// <summary>Bar collapsed or user paged away. Stop every timer. Go completely quiet.</summary>
    void OnCollapsed();
}
