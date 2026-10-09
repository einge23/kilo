using Bogus;
using Kilo.Features.Me.Requests;

namespace Kilo.Tests.Fakers;

public sealed class PreferencesRequestFaker : Faker<PreferencesRequest>
{
    public PreferencesRequestFaker()
    {
        RuleFor(request => request.TimeZone, faker => faker.PickRandom(
            "America/Phoenix", "America/New_York", "Europe/London", "Asia/Tokyo"));
        RuleFor(request => request.MeasurementSystem, faker => faker.PickRandom("imperial", "metric"));
    }
}
