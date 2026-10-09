using FluentValidation;

namespace Kilo.Features.Routines.Requests;

public sealed class RoutineWriteRequestValidator : AbstractValidator<RoutineWriteRequest>
{
    public RoutineWriteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.")
            .OverridePropertyName("name");
    }
}
