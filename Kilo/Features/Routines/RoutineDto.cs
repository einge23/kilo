using Kilo.Persistence.Entities;

namespace Kilo.Features.Routines;

public sealed record RoutineDto(int Id, string Name, string Description, DateTime? ArchivedAt)
{
    // ponytail: only empty routines in slice 06; use typed placement DTOs when slice 07 arrives.
    public object[] Exercises => [];

    public static RoutineDto From(Routine routine) =>
        new(routine.Id, routine.Name, routine.Description, routine.ArchivedAt);
}
