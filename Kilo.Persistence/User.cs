namespace Kilo.Persistence;

public sealed class User
{
    public int Id { get; set; }
    public required string ClerkUserId { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public string MeasurementSystem { get; set; } = "imperial";
    public DateTime CreatedAt { get; set; }
}
