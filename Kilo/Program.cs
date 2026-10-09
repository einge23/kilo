using Kilo.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddKiloApi();
builder.Services.AddKiloPersistence(builder.Configuration);
builder.Services.AddKiloClerk(builder.Configuration);


var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseRouting();
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();

app.Run();

public partial class Program { }
