using Bogus;
using Kilo.Features.Routines.Requests;

namespace Kilo.Tests.Fakers;

public sealed class RoutineWriteRequestFaker : Faker<RoutineWriteRequest>
{
    public RoutineWriteRequestFaker()
    {
        RuleFor(x => x.Name, faker => faker.PickRandom("Upper A", "Lower A", "Abs and Arms", "Upper B", "Lower B"));
        RuleFor(x => x.Description, faker => faker.Lorem.Sentence());
    }
}
