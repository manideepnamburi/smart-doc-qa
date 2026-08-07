using SmartDocQA.Application.UseCases;
using SmartDocQA.Infrastructure.DependencyInjection;
using SmartDocQA.Domain.Interfaces;
using SmartDocQA.Application.Configuration;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
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

// ── Infrastructure (all interfaces + implementations) ─────────────────────────
builder.Services.AddInfrastructure(builder.Configuration);

// ── Application Use Cases ─────────────────────────────────────────────────────
builder.Services.AddScoped<IngestDocumentUseCase>();
builder.Services.AddScoped<IngestFolderUseCase>();
builder.Services.AddScoped<QueryDocumentUseCase>();
builder.Services.AddScoped<DeleteDocumentsUseCase>();

// NEW: also expose it via the interface, resolving to the SAME scoped
// instance as the line above — this is what lets DocumentsController keep
// injecting the concrete IngestDocumentUseCase directly (unchanged) while
// IngestFolderUseCase injects the new IIngestDocumentUseCase, without
// creating two separate instances per request.
builder.Services.AddScoped<IIngestDocumentUseCase>(sp => sp.GetRequiredService<IngestDocumentUseCase>());
builder.Services.AddScoped<IngestFolderUseCase>();
builder.Services.AddScoped<QueryDocumentUseCase>();
builder.Services.AddScoped<DeleteDocumentsUseCase>();

// ── Logging ───────────────────────────────────────────────────────────────────
// NOTE: Deliberately NOT calling SetMinimumLevel() here. Doing so sets a hard
// floor in code that overrides per-category levels from appsettings.json
// (e.g. "SmartDocQA.Infrastructure.Retrieval.BM25KeywordIndex": "Debug").
// AddConsole() alone already reads LogLevel settings from configuration —
// builder.Logging picks up appsettings.json automatically by default.
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
});

// ── Memory Cache ──────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

//    Use Cases" -- binds the Cors config section and registers a CORS
//    policy driven entirely by appsettings, so dev vs. production is just
//    a config difference, never a code difference:

builder.Services.Configure<CorsOptions>(
    builder.Configuration.GetSection(CorsOptions.SectionName));

builder.Services.Configure<VisionExtractionOptions>(
    builder.Configuration.GetSection(VisionExtractionOptions.SectionName));

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


var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
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
