using Bogus;
using Kilo.Features.Exercises.Requests;

namespace Kilo.Tests.Fakers;

public sealed class ExerciseWriteRequestFaker : Faker<ExerciseWriteRequest>
{
    public ExerciseWriteRequestFaker()
    {
        RuleFor(x => x.Name, faker => faker.PickRandom("Bench Press", "Chest Press", "Squat", "Row"));
        RuleFor(x => x.Description, faker => faker.Lorem.Sentence());
        RuleFor(x => x.BrandName, faker => faker.Company.CompanyName());
    }
}
