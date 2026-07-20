namespace DevBar.Modules.GitStatus;

public sealed class RepoStatus
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
    public string Branch { get; set; } = "";
    public bool IsDirty { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public bool HasUpstream { get; set; }
    /// <summary>Path doesn't exist, or isn't a git repo.</summary>
    public bool Error { get; set; }
}
