namespace Kilo.Features.Routines.Requests;

public sealed class PlacementUpdateRequest
{
    public int? Position { get; init; }
    public string? Description { get; init; }
    public int? DefaultRestSeconds { get; init; }
}
