using Asp.Versioning;
using FluentValidation;
using Kilo.Features.Me;
using Kilo.Features.Routines.Requests;
using Kilo.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Kilo.Features.Routines;

[ApiController]
[Authorize]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/routines/{routineId:int:min(1)}/exercises")]
public sealed class RoutinePlacementsController(CurrentUser currentUser, RoutinePlacementService placements,
    IValidator<PlacementCreateRequest> createValidator, IValidator<PlacementUpdateRequest> updateValidator) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineExerciseDto>> Create(int routineId, PlacementCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await createValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }
        var userId = await currentUser.GetIdAsync(cancellationToken);
        var result = await placements.WriteAsync(userId, routineId, new PlacementWrite
        {
            ExerciseId = request.ExerciseId,
            Position = request.Position!.Value,
            Description = request.Description ?? "",
            DefaultRestSeconds = request.DefaultRestSeconds!.Value
        }, cancellationToken);
        return result.Placement is { } dto
            ? CreatedAtAction(nameof(RoutinesController.Get), "Routines", new { version = "1", id = routineId }, dto)
            : Failure(result.Failure!.Value);
    }

    [HttpPut("{placementId:int:min(1)}")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineExerciseDto>> Update(int routineId, int placementId, PlacementUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await updateValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }
        var userId = await currentUser.GetIdAsync(cancellationToken);
        var result = await placements.WriteAsync(userId, routineId, new PlacementWrite
        {
            PlacementId = placementId,
            Position = request.Position!.Value,
            Description = request.Description ?? "",
            DefaultRestSeconds = request.DefaultRestSeconds!.Value
        }, cancellationToken);
        return result.Placement is { } dto ? dto : Failure(result.Failure!.Value);
    }

    private ActionResult<RoutineExerciseDto> Failure(PlacementWriteFailure failure) => failure switch
    {
        PlacementWriteFailure.NotFound => NotFound(),
        PlacementWriteFailure.Archived => Problem(statusCode: StatusCodes.Status409Conflict, title: "Archived templates cannot be edited."),
        PlacementWriteFailure.PositionConflict => Problem(statusCode: StatusCodes.Status409Conflict, title: "The routine position is already occupied."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}
