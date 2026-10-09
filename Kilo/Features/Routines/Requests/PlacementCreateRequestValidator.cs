using FluentValidation;

namespace Kilo.Features.Routines.Requests;

public sealed class PlacementCreateRequestValidator : AbstractValidator<PlacementCreateRequest>
{
    public PlacementCreateRequestValidator()
    {
        RuleFor(x => x.ExerciseId).NotNull().GreaterThan(0).OverridePropertyName("exerciseId");
        RuleFor(x => x.Position).NotNull().GreaterThan(0).OverridePropertyName("position");
        RuleFor(x => x.DefaultRestSeconds).NotNull().GreaterThanOrEqualTo(0).OverridePropertyName("defaultRestSeconds");
    }
}
