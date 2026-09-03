using SmartDocQA.Application.Configuration;
using SmartDocQA.Application.DependencyInjection;
using SmartDocQA.Infrastructure.DependencyInjection;

// ─── Program.cs (Composition Root) ──────────────────────────────────────────
//
// This file's ONLY job is host bootstrapping: wire up ASP.NET Core itself
// (controllers, Swagger, logging, CORS, memory cache), then delegate every
// layer's own service registrations to that layer's own extension method
// -- AddInfrastructure() and AddApplicationServices() -- rather than
// listing individual services here.
//
// WHY THIS SPLIT (added during Phase 7.5 cleanup):
// Before this refactor, every use case, retrieval-pipeline, and Phase 7.5
// agent-pipeline registration lived directly in this file as it grew
// phase by phase -- by the time Phase 7.5 finished, this file had two
// duplicate blocks registering the same use cases twice (harmless at
// runtime, but a real maintenance smell) and no clear home for
// Application-layer registrations distinct from Infrastructure-layer
// ones. Each layer now owns and exposes its own registration method,
// called here in dependency order -- Infrastructure first (nothing in
// Application depends on anything in the API project), then
// Application. This keeps Program.cs readable regardless of how many
// more services future phases add, since growth happens inside each
// layer's own extension method instead of this file.

var builder = WebApplication.CreateBuilder(args);

// ── ASP.NET Core host services ───────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "SmartDocQA API",
        Version     = "v1",
        Description = "Advanced RAG Document Q&A — C# .NET 8 + Semantic Kernel + Claude"
    });
});

builder.Services.AddLogging(logging =>
{
    // Deliberately NOT calling SetMinimumLevel() here. Doing so sets a hard
    // floor in code that overrides per-category levels from appsettings.json
    // (e.g. "SmartDocQA.Infrastructure.Retrieval.BM25KeywordIndex": "Debug").
    // AddConsole() alone already reads LogLevel settings from configuration.
    logging.AddConsole();
});

builder.Services.AddMemoryCache();

// ── CORS ──────────────────────────────────────────────────────────────────────
// Stays here rather than in a layer extension method -- CORS policy setup
// is a genuine ASP.NET Core hosting concern (it configures the HTTP
// pipeline itself), not a Domain/Application/Infrastructure concern.
// Policy is config-driven so dev (Vite's default port) vs. production
// (a real domain, once deployed) is just an appsettings difference, never
// a code difference.
builder.Services.Configure<CorsOptions>(
    builder.Configuration.GetSection(CorsOptions.SectionName));

builder.Services.AddCors(options =>
{
    var corsOptions = builder.Configuration
        .GetSection(CorsOptions.SectionName)
        .Get<CorsOptions>() ?? new CorsOptions();

    options.AddPolicy("SmartDocQAWebClient", policy =>
    {
        policy.WithOrigins(corsOptions.AllowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ── Layer registrations ──────────────────────────────────────────────────────
// Infrastructure first, then Application -- matches the dependency
// direction (Application depends on Domain interfaces; Infrastructure
// provides their concrete implementations; neither depends on the other
// directly, but registering Infrastructure's DI container entries first
// means anything Application's registrations resolve via constructor
// injection at startup is already available).
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplicationServices(builder.Configuration);

var app = builder.Build();

// ── Middleware pipeline ──────────────────────────────────────────────────────
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