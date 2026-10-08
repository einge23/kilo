using System.ComponentModel.DataAnnotations;

namespace Kilo.Persistence.Entities;

public sealed class User
{
    public int Id { get; set; }

    [MaxLength(27)]
    public required string ClerkUserId { get; set; }

    public string TimeZone { get; set; } = "UTC";

    [MaxLength(8)]
    public string MeasurementSystem { get; set; } = "imperial";
    public DateTime CreatedAt { get; set; }
}
