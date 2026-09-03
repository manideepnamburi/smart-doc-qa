using SmartDocQA.Application.Configuration;

namespace SmartDocQA.API.DependencyInjection;

/// <summary>
/// Registers everything owned by the API layer itself: ASP.NET Core
/// framework services (controllers, Swagger, logging, memory cache) and
/// the CORS policy. Mirrors the AddInfrastructure() / AddApplicationServices()
/// pattern -- each layer owns and exposes its own registration extension
/// method, called from Program.cs in sequence.
///
/// WHAT DELIBERATELY STAYS OUT OF THIS FILE:
/// The middleware PIPELINE (app.UseSwagger(), app.UseCors(...),
/// app.UseAuthorization(), app.MapControllers()) stays inline in
/// Program.cs, not extracted here. Pipeline order is semantically
/// load-bearing -- CORS must run before auth, auth before routing --
/// and hiding that sequence inside a method name risks someone changing
/// the order later without seeing the consequence. Service REGISTRATION
/// (this file) is safe to extract because registration order mostly
/// doesn't matter; pipeline CONFIGURATION isn't, so it stays visible at
/// the composition root.
/// </summary>
public static class ApiServiceExtensions
{
    public static IServiceCollection AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── ASP.NET Core framework services ─────────────────────────────────
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new()
            {
                Title       = "SmartDocQA API",
                Version     = "v1",
                Description = "Advanced RAG Document Q&A — C# .NET 8 + Semantic Kernel + Claude"
            });
        });

        services.AddLogging(logging =>
        {
            // Deliberately NOT calling SetMinimumLevel() here. Doing so sets
            // a hard floor in code that overrides per-category levels from
            // appsettings.json (e.g. "SmartDocQA.Infrastructure.Retrieval.
            // BM25KeywordIndex": "Debug"). AddConsole() alone already reads
            // LogLevel settings from configuration.
            logging.AddConsole();
        });

        services.AddMemoryCache();

        // ── CORS ──────────────────────────────────────────────────────────
        // Policy is config-driven so dev (Vite's default port) vs.
        // production (a real domain, once deployed) is just an
        // appsettings difference, never a code difference.
        services.Configure<CorsOptions>(
            configuration.GetSection(CorsOptions.SectionName));

        services.AddCors(options =>
        {
            var corsOptions = configuration
                .GetSection(CorsOptions.SectionName)
                .Get<CorsOptions>() ?? new CorsOptions();

            options.AddPolicy("SmartDocQAWebClient", policy =>
            {
                policy.WithOrigins(corsOptions.AllowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod();
            });
        });

        return services;
    }
}