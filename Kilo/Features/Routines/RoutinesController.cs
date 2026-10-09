using Asp.Versioning;
using FluentValidation;
using Kilo.Features.Me;
using Kilo.Features.Routines.Requests;
using Kilo.Hosting;
using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Kilo.Persistence.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kilo.Features.Routines;

[ApiController]
[Authorize]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/routines")]
public sealed class RoutinesController(KiloDbContext db, CurrentUser currentUser,
    IValidator<RoutineWriteRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<RoutineSummaryDto[]>> List([FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetIdAsync(cancellationToken);
        return await db.Routines.AsNoTracking().OwnedBy(userId)
            .Where(x => includeArchived || x.ArchivedAt == null)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new RoutineSummaryDto(x.Id, x.Name, x.Description, x.ArchivedAt))
            .ToArrayAsync(cancellationToken);
    }

    [HttpGet("{id:int:min(1)}")]
    public async Task<ActionResult<RoutineDto>> Get(int id, CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetIdAsync(cancellationToken);
        var routine = await RoutineDto.Project(db.Routines.AsNoTracking().OwnedBy(userId).Where(x => x.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
        return routine is null ? NotFound() : routine;
    }

    [HttpPost]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> Create(RoutineWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }

        var userId = await currentUser.GetIdAsync(cancellationToken);
        var routine = new Routine
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description ?? ""
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { version = "1", id = routine.Id }, RoutineDto.From(routine));
    }

    [HttpPut("{id:int:min(1)}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> Update(int id, RoutineWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }

        var userId = await currentUser.GetIdAsync(cancellationToken);
        var routine = await db.Routines.OwnedBy(userId).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (routine is null)
        {
            return NotFound();
        }
        if (routine.ArchivedAt is not null)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Archived routines cannot be edited.");
        }

        routine.Name = request.Name.Trim();
        routine.Description = request.Description ?? "";
        await db.SaveChangesAsync(cancellationToken);
        return await RoutineDto.Project(db.Routines.AsNoTracking().OwnedBy(userId).Where(x => x.Id == id))
            .SingleAsync(cancellationToken);
    }
}
