using System.Globalization;
using System.Runtime.InteropServices;

var expectedArchitecture = args[0] == "linux/amd64" ? Architecture.X64 : Architecture.Arm64;
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != expectedArchitecture)
    throw new InvalidOperationException("Unexpected runtime platform.");

if (new CultureInfo("fr-FR").NumberFormat.NumberDecimalSeparator != ",")
    throw new InvalidOperationException("ICU globalization is unavailable.");

var winter = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
var summer = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
if (eastern.GetUtcOffset(winter) != TimeSpan.FromHours(-5)
    || eastern.GetUtcOffset(summer) != TimeSpan.FromHours(-4))
    throw new InvalidOperationException("IANA timezone data is unavailable.");

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
using var response = await client.GetAsync("https://learn.microsoft.com/en-us/dotnet/");
response.EnsureSuccessStatusCode();

if (File.Exists("/app/Kilo.Migrations.dll"))
{
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
        var path = Path.Combine("/app", name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    };
    var assembly = System.Runtime.Loader.AssemblyLoadContext.Default
        .LoadFromAssemblyPath("/app/Kilo.Persistence.dll");
    var migrations = assembly.GetTypes().SelectMany(type => type.CustomAttributes)
        .Where(attribute => attribute.AttributeType.FullName ==
            "Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute")
        .Select(attribute => (string)attribute.ConstructorArguments[0].Value!).ToArray();
    if (!migrations.Any(id => id.EndsWith("_CreateUsers", StringComparison.Ordinal)))
        throw new InvalidOperationException("Compiled EF users migration is missing.");
    Console.WriteLine($"Compiled EF migrations: {migrations.Length}");
}

Console.WriteLine("Runtime platform, trusted HTTPS, ICU, and IANA timezones passed.");
