namespace Kilo.Features.Me.Requests;

public sealed class PreferencesRequest
{
    public string TimeZone { get; init; } = "";
    public string MeasurementSystem { get; init; } = "";
}
