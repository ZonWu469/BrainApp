using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using AspNetCoreRateLimit;
using Serilog;
using BrainApp.Core.Config;
using BrainApp.Core.Services;
using BrainApp.Core.Skills;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/brainapp-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add Memory Cache
builder.Services.AddMemoryCache();

// Register Core services (singleton, share across all requests)
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<LlamaService>();
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<ProfileRepository>();
builder.Services.AddSingleton<IndexingStatusService>();
builder.Services.AddSingleton<SkillScriptEngine>();
builder.Services.AddSingleton<SkillExecutor>();
builder.Services.AddSingleton<SkillFetchCache>();
builder.Services.AddSingleton<SkillCatalogService>();

// Register configuration sections as options
builder.Services.Configure<LlamaSettings>(builder.Configuration.GetSection("LLama"));
builder.Services.Configure<CacheSettings>(builder.Configuration.GetSection("Cache"));
builder.Services.Configure<RetrievalSettings>(builder.Configuration.GetSection("Retrieval"));
builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<ApiSettings>(builder.Configuration.GetSection("Api"));
builder.Services.Configure<SkillsSettings>(builder.Configuration.GetSection("Skills"));

// Add controllers
builder.Services.AddControllers();

// Configure Swagger with X-Api-Key security definition
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "BrainApp API",
        Version = "v1",
        Description = "Offline knowledge base API powered by in-process LLamaSharp GGUF inference"
    });

    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API Key authentication. Example: 'X-Api-Key: change-me-in-production'",
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "ApiKeyScheme"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

// Configure API Key authentication middleware
var apiSettings = new ApiSettings();
builder.Configuration.GetSection("Api").Bind(apiSettings);

builder.Services.AddAuthentication("ApiKey")
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null);

builder.Services.AddAuthorization();

// Configure rate limiting
builder.Services.Configure<IpRateLimitOptions>(options =>
{
    options.GeneralRules = new List<RateLimitRule>
    {
        new RateLimitRule
        {
            Endpoint = "*:/health",
            Period = "1m",
            Limit = 120
        },
        new RateLimitRule
        {
            Endpoint = "*",
            Period = "1m",
            Limit = apiSettings.RateLimitPerMinute > 0 ? apiSettings.RateLimitPerMinute : 60
        }
    };
});

builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddInMemoryRateLimiting();

var app = builder.Build();

// Crash recovery: flip stuck 'Indexing' docs to 'Error' so they're retryable.
try
{
    app.Services.GetRequiredService<ProfileRepository>().ResetStuckDocuments();
}
catch (Exception ex)
{
    Log.Warning(ex, "Failed to reset stuck Indexing documents on startup");
}

// Initialize LLamaSharp before accepting requests
var llamaService = app.Services.GetRequiredService<LlamaService>();
try
{
    Log.Information("Loading AI models...");
    await llamaService.InitializeAsync();
    Log.Information("LLamaSharp initialized successfully");
}
catch (FileNotFoundException ex)
{
    Log.Warning(ex, "Model file not found. Some endpoints may not work until a GGUF model is placed in the models/ folder.");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "BrainApp API v1");
    });
}

// Enable client IP rate limiting
app.UseIpRateLimiting();

// Routing and endpoints
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Log the port from settings
var port = apiSettings.Port > 0 ? apiSettings.Port : 5199;
Log.Information("BrainApp API starting on port {Port}", port);
app.Run($"http://0.0.0.0:{port}");

/// <summary>
/// Simple API Key authentication handler for BrainApp.
/// Validates the X-Api-Key header against the configured ApiKey.
/// </summary>
public class ApiKeyAuthenticationHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    private readonly string _apiKey;

    public ApiKeyAuthenticationHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _apiKey = configuration["Api:ApiKey"] ?? "change-me-in-production";
    }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var keyHeader))
        {
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
        }

        if (keyHeader != _apiKey)
        {
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Fail("Invalid API Key"));
        }

        var claims = new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "ApiKeyUser"),
            new System.Security.Claims.Claim("ApiKey", keyHeader!)
        };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, Scheme.Name);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}