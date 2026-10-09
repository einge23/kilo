using Bogus;
using Kilo.Features.Routines.Requests;

namespace Kilo.Tests.Fakers;

public sealed class PlacementCreateRequestFaker : Faker<PlacementCreateRequest>
{
    public PlacementCreateRequestFaker()
    {
        RuleFor(x => x.ExerciseId, f => f.Random.Int(1, 1000));
        RuleFor(x => x.Position, f => f.Random.Int(1, 10));
        RuleFor(x => x.Description, f => f.Lorem.Sentence());
        RuleFor(x => x.DefaultRestSeconds, f => f.Random.Int(0, 180));
    }
}
