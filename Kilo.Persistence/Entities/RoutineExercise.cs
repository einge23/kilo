namespace Kilo.Persistence.Entities;

public sealed class RoutineExercise
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int RoutineId { get; set; }
    public int ExerciseId { get; set; }
    public int ExerciseScopeId { get; set; }
    public int Position { get; set; }
    public string Description { get; set; } = "";
    public int DefaultRestSeconds { get; set; } = 120;
    public DateTime? ArchivedAt { get; set; }
    public Exercise Exercise { get; set; } = null!;
}
