using FluentValidation;

namespace Kilo.Features.Routines.Requests;

public sealed class PlacementUpdateRequestValidator : AbstractValidator<PlacementUpdateRequest>
{
    public PlacementUpdateRequestValidator()
    {
        RuleFor(x => x.Position).NotNull().GreaterThan(0).OverridePropertyName("position");
        RuleFor(x => x.DefaultRestSeconds).NotNull().GreaterThanOrEqualTo(0).OverridePropertyName("defaultRestSeconds");
    }
}
