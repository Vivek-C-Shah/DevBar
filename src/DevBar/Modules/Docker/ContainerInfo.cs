namespace DevBar.Modules.Docker;

public sealed class ContainerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string Status { get; init; }
    public bool IsRunning => Status.StartsWith("Up", StringComparison.OrdinalIgnoreCase);
}
