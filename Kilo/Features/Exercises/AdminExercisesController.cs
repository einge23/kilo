using Asp.Versioning;
using FluentValidation;
using Kilo.Features.Exercises.Requests;
using Kilo.Hosting;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kilo.Features.Exercises;

[ApiController]
[Authorize(Policy = "Admin")]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/admin/exercises")]
public sealed class AdminExercisesController(KiloDbContext db,
    IValidator<ExerciseWriteRequest> validator) : ControllerBase
{
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

        var exercise = new Exercise
        {
            Name = request.Name.Trim(),
            Description = request.Description ?? "",
            BrandName = request.NormalizedBrandName
        };
        db.Exercises.Add(exercise);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(ExercisesController.Get), "Exercises", new { version = "1", id = exercise.Id },
            ExerciseDto.From(exercise));
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

        var exercise = await db.Exercises.SingleOrDefaultAsync(x => x.Id == id && x.UserId == null, cancellationToken);
        if (exercise is null)
        {
            return NotFound();
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
