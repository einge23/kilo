namespace Kilo.Features.Exercises.Requests;

public sealed class ExerciseWriteRequest
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? BrandName { get; init; }

    internal string? NormalizedBrandName => string.IsNullOrWhiteSpace(BrandName) ? null : BrandName.Trim();
}
