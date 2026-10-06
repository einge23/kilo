using Kilo.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKiloApi();
builder.Services.AddKiloPersistence(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllers();
app.Run();

public partial class Program { }
