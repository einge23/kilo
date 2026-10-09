namespace Kilo.Persistence.Entities;

public sealed class Routine
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public ICollection<RoutineExercise> Exercises { get; } = [];
}
