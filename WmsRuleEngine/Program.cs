using WmsRuleEngine.Application.Services;
using WmsRuleEngine.Infrastructure.Ollama;
using WmsRuleEngine.Infrastructure.RAG;
using WmsRuleEngine.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection(OllamaOptions.SectionName));

// ─── Infrastructure ────────────────────────────────────────────────────────────

// Ollama HTTP client with resilience
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));

// RAG schema store (singleton - seeded once)
builder.Services.AddSingleton<WmsSchemaStore>();

// Prompt builder and parser
builder.Services.AddSingleton<RulePromptBuilder>();
builder.Services.AddScoped<RuleJsonParser>();

// Repository - swap with EF Core in production:
// builder.Services.AddDbContext<WmsDbContext>(...)
// builder.Services.AddScoped<IRuleRepository, EfRuleRepository>();
builder.Services.AddSingleton<IRuleRepository, InMemoryRuleRepository>();

// ─── Application Services ──────────────────────────────────────────────────────
builder.Services.AddScoped<IRuleGeneratorService, RuleGeneratorService>();
builder.Services.AddScoped<IRuleManagementService, RuleManagementService>();

// ─── API ────────────────────────────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.WriteIndented = true;
        // Handle object type for rightOperand
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "WMS Rule Engine API", Version = "v1" });
});

builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.MapControllers();

// Health check endpoint
app.MapGet("/health", async (IOllamaClient ollama) =>
{
    var ollamaOk = await ollama.IsAvailableAsync();
    return ollamaOk
        ? Results.Ok(new { status = "healthy", ollama = "connected" })
        : Results.Json(new { status = "degraded", ollama = "unreachable" }, statusCode: 503);
});

app.Run();
