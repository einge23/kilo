using FluentValidation;

namespace Kilo.Features.Me.Requests;

public sealed class PreferencesRequestValidator : AbstractValidator<PreferencesRequest>
{
    public PreferencesRequestValidator()
    {
        RuleFor(x => x.TimeZone).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Time zone is required.")
            .Must(id => TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
                && (id == "UTC" || zone.HasIanaId))
            .WithMessage("Use a supported IANA time zone.")
            .OverridePropertyName("timeZone");

        RuleFor(x => x.MeasurementSystem).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Measurement system is required.")
            .Must(value => value is "imperial" or "metric")
            .WithMessage("Use imperial or metric.")
            .OverridePropertyName("measurementSystem");
    }
}
