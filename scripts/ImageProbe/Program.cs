using System.Globalization;
using System.Reflection;
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
    var assembly = Assembly.LoadFile("/app/Kilo.Migrations.dll");
    var scripts = assembly.GetManifestResourceNames()
        .Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).ToArray();
    if (!scripts.Any(name => name.EndsWith(".001_users.sql", StringComparison.Ordinal)))
        throw new InvalidOperationException("Embedded users migration is missing.");
    foreach (var script in scripts)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(script)!);
        if (string.IsNullOrWhiteSpace(await reader.ReadToEndAsync()))
            throw new InvalidOperationException($"Empty embedded migration: {script}");
    }
    Console.WriteLine($"Embedded migrations: {scripts.Length}");
}

Console.WriteLine("Runtime platform, trusted HTTPS, ICU, and IANA timezones passed.");
