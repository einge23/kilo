namespace Kilo.Features.Me;

public sealed record UserDto
{
    public int Id { get; init; }

    public required string ClerkUserId { get; init; }

    public required string TimeZone { get; init; }

    public required string MeasurementSystem { get; init; }
}
