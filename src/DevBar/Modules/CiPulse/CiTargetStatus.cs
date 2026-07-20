namespace DevBar.Modules.CiPulse;

public enum CiState { Idle, Success }

public sealed class CiTargetStatus
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public CiState State { get; set; } = CiState.Idle;
    public DateTime? LastModified { get; set; }
}
