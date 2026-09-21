using System.Collections.ObjectModel;
using System.Windows.Controls;
using DevBar.Sdk;

namespace DevBar.Modules.Shelf;

/// <summary>
/// Ephemeral drag-and-drop scratch space. In-memory only, by design - it
/// resets on restart. This is a shelf, not a filing cabinet.
/// </summary>
public sealed class ShelfModule : IDevBarModule
{
    public string Id => "shelf";
    public string DisplayName => "Shelf";
    public string IconGlyph => "";

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public ShelfModule(IReadOnlyList<string> seedPaths)
    {
        foreach (var p in seedPaths)
            if (System.IO.File.Exists(p) || System.IO.Directory.Exists(p))
                Items.Add(new ShelfItem { Path = p });
    }

    public void Add(string path) => Items.Add(new ShelfItem { Path = path });
    public void Remove(ShelfItem item) => Items.Remove(item);
    public void Clear() => Items.Clear();

    public UserControl BuildCard() => new ShelfCard(this);
    public void OnExpanded() { }
    public void OnCollapsed() { }
}
