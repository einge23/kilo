using Asp.Versioning;
using FluentValidation;
using Kilo.Features.Me.Requests;
using Kilo.Hosting;
using Kilo.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kilo.Features.Me;

[ApiController]
[Authorize]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController(
    KiloDbContext db,
    CurrentUser currentUser,
    IValidator<PreferencesRequest> validator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<UserDto>> Get(CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetIdAsync(cancellationToken);

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new UserDto
            {
                Id = user.Id,
                ClerkUserId = user.ClerkUserId,
                TimeZone = user.TimeZone,
                MeasurementSystem = user.MeasurementSystem,
            })
            .SingleAsync(cancellationToken);
    }

    [HttpPut("preferences")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UserDto>> UpdatePreferences(PreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return this.RequestValidationProblem(validation);
        }

        var userId = await currentUser.GetIdAsync(cancellationToken);

        var user = await db.Users.SingleAsync(user => user.Id == userId, cancellationToken);

        user.TimeZone = request.TimeZone;
        user.MeasurementSystem = request.MeasurementSystem;

        await db.SaveChangesAsync(cancellationToken);

        return new UserDto
        {
            Id = user.Id,
            ClerkUserId = user.ClerkUserId,
            TimeZone = user.TimeZone,
            MeasurementSystem = user.MeasurementSystem,
        };
    }
}
