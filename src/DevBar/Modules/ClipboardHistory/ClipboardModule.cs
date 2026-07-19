using System.Collections.ObjectModel;
using System.Windows.Controls;
using DevBar.Core;
using DevBar.Sdk;

namespace DevBar.Modules.ClipboardHistory;

public sealed class ClipboardModule : IDevBarModule
{
    public string Id => "clipboard";
    public string DisplayName => "Clipboard";
    public string IconGlyph => "";

    private readonly Config _config;
    public ObservableCollection<ClipboardEntry> Entries { get; } = new();

    public ClipboardModule(Config config) => _config = config;

    public void AddEntry(string text)
    {
        // de-dupe consecutive identical copies (common when re-copying the same selection)
        if (Entries.Count > 0 && Entries[0].Text == text) return;

        Entries.Insert(0, new ClipboardEntry { Text = text, Kind = ClipboardEntry.Classify(text) });
        while (Entries.Count > _config.ClipboardHistorySize)
            Entries.RemoveAt(Entries.Count - 1);
    }

    public UserControl BuildCard() => new ClipboardCard(this);

    public void OnExpanded() { /* purely event-driven, nothing to start */ }
    public void OnCollapsed() { }
}
