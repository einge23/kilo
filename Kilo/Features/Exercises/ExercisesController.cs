using Asp.Versioning;
using FluentValidation;
using Kilo.Features.Exercises.Requests;
using Kilo.Features.Me;
using Kilo.Hosting;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kilo.Features.Exercises;

[ApiController]
[Authorize]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/exercises")]
public sealed class ExercisesController(KiloDbContext db, CurrentUser currentUser,
    IValidator<ExerciseWriteRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ExerciseDto[]>> List([FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetIdAsync(cancellationToken);
        return await db.Exercises.AsNoTracking()
            .Where(x => (x.UserId == null || x.UserId == userId) && (includeArchived || x.ArchivedAt == null))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new ExerciseDto(x.Id, x.Name, x.Description, x.BrandName, x.UserId == null, x.ArchivedAt))
            .ToArrayAsync(cancellationToken);
    }

    [HttpGet("{id:int:min(1)}")]
    public async Task<ActionResult<ExerciseDto>> Get(int id, CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetIdAsync(cancellationToken);
        var exercise = await db.Exercises.AsNoTracking()
            .Where(x => x.Id == id && (x.UserId == null || x.UserId == userId))
            .Select(x => new ExerciseDto(x.Id, x.Name, x.Description, x.BrandName, x.UserId == null, x.ArchivedAt))
            .SingleOrDefaultAsync(cancellationToken);
        return exercise is null ? NotFound() : exercise;
    }

    [HttpPost]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExerciseDto>> Create(ExerciseWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }

        var userId = await currentUser.GetIdAsync(cancellationToken);
        var exercise = new Exercise
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description ?? "",
            BrandName = request.NormalizedBrandName
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { version = "1", id = exercise.Id }, ExerciseDto.From(exercise));
    }

    [HttpPut("{id:int:min(1)}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ExerciseDto>> Update(int id, ExerciseWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }

        var userId = await currentUser.GetIdAsync(cancellationToken);
        var exercise = await db.Exercises.OwnedBy(userId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (exercise is null)
        {
            return await db.Exercises.AnyAsync(x => x.Id == id && x.UserId == null, cancellationToken)
                ? Forbid() : NotFound();
        }
        if (exercise.ArchivedAt is not null)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Archived exercises cannot be edited.");
        }

        exercise.Name = request.Name.Trim();
        exercise.Description = request.Description ?? "";
        exercise.BrandName = request.NormalizedBrandName;
        await db.SaveChangesAsync(cancellationToken);
        return ExerciseDto.From(exercise);
    }
}
