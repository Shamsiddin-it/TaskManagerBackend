using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorTaskFlow.Api.Authentication;
using MentorTaskFlow.Api.Extensions;
using MentorTaskFlow.Api.HealthChecks;
using MentorTaskFlow.Api.Middleware;
using MentorTaskFlow.Api.Options;
using MentorTaskFlow.Api.Tenancy;
using MentorTaskFlow.Application.Common.Tenancy;
using MentorTaskFlow.Infrastructure;
using MentorTaskFlow.Infrastructure.Identity;
using MentorTaskFlow.Infrastructure.Options;
using MentorTaskFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

// ---------------------------------------------------------------------------
// Logging — Serilog, JSON, correlation-aware (OBS-001, OBS-002).
// ---------------------------------------------------------------------------
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName))
    .ValidateOnStart();

// Read lazily inside the policy builder below. ConfigurationManager is live, so a read at that point
// includes every source added after this line — which is what makes the setting overridable by tests
// and by a late-bound secret provider alike.
CorsOptions ReadCorsOptions() =>
    builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();

// ---------------------------------------------------------------------------
// MVC + JSON contract (API-003, API-005)
// ---------------------------------------------------------------------------
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // Strict deserialization: an unknown member anywhere in the payload, including nested
        // objects, is rejected with 400 VALIDATION_FAILED (API-005). This is what makes SEC-003
        // enforceable — a request carrying `organizationId` or `role` fails instead of being
        // silently ignored.
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;

        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddMentorTaskFlowValidationProblem();

// ---------------------------------------------------------------------------
// OpenAPI (API-002)
// ---------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "MentorTaskFlow API",
        Version = "v1",
        Description = "MentorTaskFlow Release 1.0 backend. Contract source: MentorTaskFlow_TZ_v2.2.md.",
    });
});

// ---------------------------------------------------------------------------
// CORS (SEC-006) — fail closed when no origin is configured.
// ---------------------------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsOptions.PolicyName, policy =>
    {
        var corsOptions = ReadCorsOptions();

        if (corsOptions.AllowedOrigins.Length == 0)
        {
            // No origin configured: no browser cross-origin request is accepted. A wildcard origin
            // is never used, not even in Development, because AllowCredentials forbids it.
            policy.WithOrigins();
            return;
        }

        policy.WithOrigins(corsOptions.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .WithExposedHeaders(CorrelationIdMiddleware.HeaderName);
    });
});

// ---------------------------------------------------------------------------
// Tenancy (TZ 38.3). ICurrentUserContext comes from token claims; IBranchContext
// is the effective scope computed per request by BranchContextMiddleware.
// ---------------------------------------------------------------------------
builder.Services.AddOptions<TenancyOptions>()
    .Bind(builder.Configuration.GetSection(TenancyOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
builder.Services.AddScoped<RequestBranchContext>();
builder.Services.AddScoped<IBranchContext>(sp => sp.GetRequiredService<RequestBranchContext>());

// ---------------------------------------------------------------------------
// Authentication (TZ 16). HS256 bearer tokens; the refresh token lives only in
// the mtf_rt cookie and is never validated by this handler.
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// The validation parameters come from IOptions<AuthOptions>, not from an eager read of
// builder.Configuration — see ConfigureJwtBearerOptions for why that distinction matters.
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
builder.Services.AddScoped<MtfJwtBearerEvents>();
builder.Services.AddAuthorization();
builder.Services.AddSingleton<AuthCookieManager>();
builder.Services.AddMentorTaskFlowRateLimiting();

// ---------------------------------------------------------------------------
// HSTS (SEC-008) — max-age 31536000, includeSubDomains.
// ---------------------------------------------------------------------------
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

// ---------------------------------------------------------------------------
// Infrastructure
// ---------------------------------------------------------------------------
builder.Services.AddInfrastructure(builder.Configuration, isDevelopment);

// ---------------------------------------------------------------------------
// Health checks (OBS-003, OBS-004)
// ---------------------------------------------------------------------------
var healthChecks = builder.Services.AddHealthChecks();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    healthChecks.AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);
}

var app = builder.Build();

// ---------------------------------------------------------------------------
// mtf-migrator mode (DEPLOY-016, DEPLOY-022). The same image, invoked as
//   dotnet MentorTaskFlow.Api.dll --migrate
// applies migrations, provisions the first tenant and exits. Keeping this out of
// the API startup path is the whole point: several API replicas booting at once
// would race for schema locks.
// ---------------------------------------------------------------------------
if (args.Contains("--migrate"))
{
    using var migrationScope = app.Services.CreateScope();
    var services = migrationScope.ServiceProvider;
    var migratorLogger = services.GetRequiredService<ILogger<Program>>();

    await services.GetRequiredService<MentorTaskFlowDbContext>().Database.MigrateAsync();
    migratorLogger.LogInformation("Migrations applied.");

    var bootstrap = await services.GetRequiredService<BootstrapProvisioner>().ProvisionAsync(CancellationToken.None);

    if (!bootstrap.Provisioned)
    {
        // Skipping is the normal path on every deploy after the first, so it is not an error
        // (DEPLOY-032).
        migratorLogger.LogInformation("Bootstrap skipped: {Reason}", bootstrap.SkipReason);
    }

    return;
}

// ---------------------------------------------------------------------------
// Migrations. Development convenience only — DatabaseOptionsValidator refuses to
// start when this is enabled anywhere else, because several API replicas booting
// at once would race for schema locks (DEPLOY-016). Test, Staging and Production
// apply migrations exclusively through the mtf-migrator container.
// ---------------------------------------------------------------------------
if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MentorTaskFlowDbContext>().Database.MigrateAsync();
}

// ---------------------------------------------------------------------------
// Pipeline. Order matters: correlation first, so every later log line and every
// ProblemDetails traceId carries the same identifier.
// ---------------------------------------------------------------------------
app.UseCorrelationId();
app.UseSerilogRequestLogging();
app.UseMentorTaskFlowExceptionHandling();
app.UseSecurityHeaders();

if (!isDevelopment)
{
    // HSTS max-age 31536000; includeSubDomains (SEC-008).
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors(CorsOptions.PolicyName);

// Authentication lands in Phase 2. Until then the middleware below sees no principal and leaves the
// tenant filter fail-closed, which is the correct behaviour for an API with no data endpoints yet.
app.UseAuthentication();
app.UseAuthorization();

// After authentication: the effective scope is derived from validated claims, never from the raw
// request (TEN-030a).
app.UseBranchContext();

if (isDevelopment)
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "MentorTaskFlow API v1"));
}

app.MapControllers();

// Liveness never touches a dependency: a failing database must not restart the process (OBS-003).
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteAsync,
});

app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync,
});

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in integration tests.</summary>
public partial class Program;
