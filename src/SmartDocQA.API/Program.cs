using SmartDocQA.API.DependencyInjection;
using SmartDocQA.Application.DependencyInjection;
using SmartDocQA.Infrastructure.DependencyInjection;

// ─── Program.cs (Composition Root) ──────────────────────────────────────────
//
// A pure composition root: three calls to register each layer's own
// services (API, Infrastructure, Application), then the middleware
// pipeline -- kept visible and inline here since pipeline ORDER is
// semantically load-bearing (CORS before auth, auth before routing),
// unlike service registration order, which mostly doesn't matter and is
// safe to delegate to each layer's own extension method.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApiServices(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplicationServices(builder.Configuration);

var app = builder.Build();

// ── Middleware pipeline — order matters, kept visible here ──────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("SmartDocQAWebClient");
app.UseAuthorization();
app.MapControllers();

app.Run();