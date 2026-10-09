using System.Data;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kilo.Features.Routines;

public sealed class RoutinePlacementService(KiloDbContext db)
{
    // Called after request validation. An absent PlacementId creates; updates never change ExerciseId.
    public async Task<PlacementWriteResult> WriteAsync(int userId, int routineId, PlacementWrite write,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var routine = await db.Routines.FromSqlInterpolated($"""
            SELECT * FROM public.routines WHERE id = {routineId} AND user_id = {userId} FOR UPDATE
            """).AsTracking().SingleOrDefaultAsync(cancellationToken);
        if (routine is null)
        {
            return new(null, PlacementWriteFailure.NotFound);
        }
        if (routine.ArchivedAt is not null)
        {
            return new(null, PlacementWriteFailure.Archived);
        }

        RoutineExercise placement;
        if (write.PlacementId is null)
        {
            // Keep routine-before-exercise ordering for future archive/attach workflows too.
            var exercise = await db.Exercises.FromSqlInterpolated($"""
                SELECT * FROM public.exercises
                WHERE id = {write.ExerciseId} AND (user_id IS NULL OR user_id = {userId}) FOR UPDATE
                """).AsTracking().SingleOrDefaultAsync(cancellationToken);
            if (exercise is null)
            {
                return new(null, PlacementWriteFailure.NotFound);
            }
            if (exercise.ArchivedAt is not null)
            {
                return new(null, PlacementWriteFailure.Archived);
            }
            placement = new RoutineExercise
            {
                UserId = userId,
                RoutineId = routineId,
                ExerciseId = exercise.Id,
                ExerciseScopeId = exercise.UserId ?? 0,
                Exercise = exercise
            };
            db.RoutineExercises.Add(placement);
        }
        else
        {
            var existing = await db.RoutineExercises.OwnedBy(userId).Include(x => x.Exercise)
                .SingleOrDefaultAsync(x => x.Id == write.PlacementId && x.RoutineId == routineId, cancellationToken);
            if (existing is null)
            {
                return new(null, PlacementWriteFailure.NotFound);
            }
            if (existing.ArchivedAt is not null)
            {
                return new(null, PlacementWriteFailure.Archived);
            }
            placement = existing;
        }

        placement.Position = write.Position;
        placement.Description = write.Description;
        placement.DefaultRestSeconds = write.DefaultRestSeconds;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "routine_exercises_active_position" })
        {
            return new(null, PlacementWriteFailure.PositionConflict);
        }

        return new(RoutineExerciseDto.From(placement), null);
    }
}

// Internal workflow input, constructed from validated HTTP requests; identity comes from separate arguments.
public sealed record PlacementWrite
{
    public int? PlacementId { get; init; }
    public int? ExerciseId { get; init; }
    public required int Position { get; init; }
    public required string Description { get; init; }
    public required int DefaultRestSeconds { get; init; }
}

public enum PlacementWriteFailure { NotFound, Archived, PositionConflict }
public sealed record PlacementWriteResult(RoutineExerciseDto? Placement, PlacementWriteFailure? Failure);
