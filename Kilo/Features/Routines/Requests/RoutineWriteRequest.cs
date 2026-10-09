namespace Kilo.Features.Routines.Requests;

public sealed class RoutineWriteRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
}
