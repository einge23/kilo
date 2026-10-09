using FluentValidation;

namespace Kilo.Features.Exercises.Requests;

public sealed class ExerciseWriteRequestValidator : AbstractValidator<ExerciseWriteRequest>
{
    public ExerciseWriteRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required.")
            .OverridePropertyName("name");
        RuleFor(x => x.BrandName).Must(value => value is null || value.Trim().Length <= 100)
            .WithMessage("Brand must be at most 100 characters.")
            .OverridePropertyName("brandName");
    }
}
