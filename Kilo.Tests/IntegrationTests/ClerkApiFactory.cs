using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Kilo.Tests.IntegrationTests;

public sealed class ClerkApiFactory : KiloApiFactory
{
    public const string Issuer = "https://clerk.kilo.test";
    public const string Origin = "https://frontend.kilo.test";
    public SigningMetadata Metadata { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
            });
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = Metadata);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                ((ConfigurationManager<OpenIdConnectConfiguration>)options.ConfigurationManager!)
                    .RefreshInterval = TimeSpan.FromSeconds(1));
            services.AddAuthorizationBuilder().AddPolicy("TestDenied", policy =>
                policy.RequireClaim("permission", "test"));
            services.AddControllers().AddApplicationPart(typeof(AuthPolicyProbeController).Assembly);
        });
    }

    public string Token(string subject, string variant = "valid", RsaSecurityKey? key = null, string? role = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["azp"] = variant == "wrong-azp" ? "https://foreign.kilo.test" : Origin
        };
        if (variant != "missing-sub")
        {
            claims["sub"] = subject;
        }

        if (role is not null)
        {
            claims["role"] = role;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = variant == "wrong-issuer" ? "https://foreign.kilo.test" : Issuer,
            Audience = variant == "wrong-audience" ? "foreign" : "kilo-tests",
            Claims = claims,
            IssuedAt = now.AddMinutes(-2),
            NotBefore = now.AddMinutes(-2),
            Expires = variant == "missing-expiration" ? null
                : variant == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key ?? Metadata.Key,
                variant == "wrong-algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256)
        });
    }

    public sealed class SigningMetadata : HttpMessageHandler
    {
        private readonly RSAParameters _second;
        public RsaSecurityKey Key { get; private set; }
        public bool Unavailable { get; set; }

        public SigningMetadata()
        {
            using var first = RSA.Create(2048);
            using var second = RSA.Create(2048);
            Key = new RsaSecurityKey(first.ExportParameters(true)) { KeyId = "first" };
            _second = second.ExportParameters(true);
        }
        public void Rotate() => Key = new RsaSecurityKey(_second) { KeyId = "second" };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Unavailable)
            {
                throw new HttpRequestException("Fixture metadata is unavailable.");
            }

            var content = request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => JsonContent.Create(new
                {
                    issuer = Issuer,
                    jwks_uri = Issuer + "/jwks"
                }),
                "/jwks" => JsonContent.Create(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(
                    new RSAParameters { Modulus = Key.Parameters.Modulus, Exponent = Key.Parameters.Exponent })
                    { KeyId = Key.KeyId }) } }),
                _ => throw new InvalidOperationException("Unexpected metadata route.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }


    }
}

// Included through the test assembly only; production never exposes this route.
[ApiController]
[Authorize(Policy = "TestDenied")]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/test/denied")]
public sealed class AuthPolicyProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
