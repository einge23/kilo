namespace Kilo.Features.Routines.Requests;

public sealed class PlacementCreateRequest
{
    public int? ExerciseId { get; init; }
    public int? Position { get; init; }
    public string? Description { get; init; }
    public int? DefaultRestSeconds { get; init; }
}
