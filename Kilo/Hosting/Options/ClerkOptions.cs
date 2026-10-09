namespace Kilo.Hosting.Options;

public sealed class ClerkOptions
{
    public string Issuer { get; set; } = "";
    public string? Audience { get; set; }
    public string[] AuthorizedParties { get; set; } = [];
}
