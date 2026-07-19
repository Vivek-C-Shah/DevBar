namespace DevBar.Modules.Shelf;

public sealed class ShelfItem
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsImage => Ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    private string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}
