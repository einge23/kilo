using Kilo.Persistence.Entities;

namespace Kilo.Features.Routines;

public sealed record RoutineExerciseDto(int Id, int ExerciseId, string ExerciseName, string? BrandName,
    int Position, string Description, int DefaultRestSeconds)
{
    // ponytail: no planned sets in slice 07; replace with typed set DTOs in slice 08.
    public object[] Sets => [];

    public static RoutineExerciseDto From(RoutineExercise placement) => new(placement.Id, placement.ExerciseId,
        placement.Exercise.Name, placement.Exercise.BrandName, placement.Position, placement.Description, placement.DefaultRestSeconds);
}
