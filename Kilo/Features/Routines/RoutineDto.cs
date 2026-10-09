using Kilo.Persistence.Entities;

namespace Kilo.Features.Routines;

public sealed record RoutineDto(int Id, string Name, string Description, DateTime? ArchivedAt)
{
    public RoutineExerciseDto[] Exercises { get; init; } = [];

    public static RoutineDto From(Routine routine) =>
        new(routine.Id, routine.Name, routine.Description, routine.ArchivedAt);

    public static IQueryable<RoutineDto> Project(IQueryable<Routine> routines) =>
        routines.Select(routine => new RoutineDto(routine.Id, routine.Name, routine.Description, routine.ArchivedAt)
        {
            Exercises = routine.Exercises.Where(x => x.ArchivedAt == null).OrderBy(x => x.Position)
                .Select(x => new RoutineExerciseDto(x.Id, x.ExerciseId, x.Exercise.Name, x.Exercise.BrandName,
                    x.Position, x.Description, x.DefaultRestSeconds)).ToArray()
        });
}
