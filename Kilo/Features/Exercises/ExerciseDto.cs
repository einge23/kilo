using Kilo.Persistence.Entities;

namespace Kilo.Features.Exercises;

public sealed record ExerciseDto(int Id, string Name, string Description, string? BrandName, bool IsGlobal, DateTime? ArchivedAt)
{
    public static ExerciseDto From(Exercise exercise) => new(exercise.Id, exercise.Name,
        exercise.Description, exercise.BrandName, exercise.UserId is null, exercise.ArchivedAt);
}
