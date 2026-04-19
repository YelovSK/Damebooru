using Damebooru.Core.Config;
using Damebooru.Core.Paths;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing;
using Damebooru.Processing.Logging;
using Damebooru.Processing.Services;
using Damebooru.Processing.Services.AutoTagging;
using Damebooru.Processing.Services.Duplicates;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);
var damebooruConfig = builder.Configuration.GetSection(DamebooruConfig.SectionName).Get<DamebooruConfig>() ?? new DamebooruConfig();
TrimSecretLikeValues(damebooruConfig);
var authEnabled = damebooruConfig.Auth.Enabled;
var trustForwardedHeaders = damebooruConfig.Proxy.TrustForwardedHeaders;

builder.Services.Configure<DamebooruConfig>(builder.Configuration.GetSection(DamebooruConfig.SectionName));
builder.Services.PostConfigure<DamebooruConfig>(config => TrimSecretLikeValues(config));

// Add services to the container.
builder.Services.AddControllers();

if (authEnabled)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "damebooru_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(7);

            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api")
                        || context.Request.Path.StartsWithSegments(MediaPaths.ThumbnailsRequestPath))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api")
                        || context.Request.Path.StartsWithSegments(MediaPaths.ThumbnailsRequestPath))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }
            };
        });
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth-login", context =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
});

builder.Services.AddAuthorization(options =>
{
    if (authEnabled)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
});

if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular",
        policy =>
        {
            policy.WithOrigins("http://localhost:4200")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials(); // Required for SignalR
        });
});

// Database
var resolvedConnectionString = StoragePathResolver.ResolveSqliteConnectionString(
    builder.Environment.ContentRootPath,
    builder.Configuration.GetConnectionString("DefaultConnection"),
    damebooruConfig.Storage.DatabasePath);

builder.Services.AddDbContextFactory<DamebooruDbContext>(options =>
    options.UseSqlite(
        resolvedConnectionString,
        sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<DamebooruDbContext>>().CreateDbContext());
builder.Services.AddScoped<PostReadService>();
builder.Services.AddScoped<PostWriteService>();
builder.Services.AddScoped<PostContentService>();
builder.Services.AddScoped<PostAutoTaggingService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<LibraryBrowseService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<DuplicateWriteService>();
builder.Services.AddScoped<DuplicateReadService>();
builder.Services.AddScoped<DuplicateLookupService>();
builder.Services.AddScoped<JobScheduleService>();
builder.Services.AddScoped<SystemReadService>();

if (damebooruConfig.Logging.Db.Enabled)
{
    builder.Services.AddSingleton(new AppLogChannel(damebooruConfig.Logging.Db.ChannelCapacity));
    builder.Services.AddSingleton<ILoggerProvider, DbLoggerProvider>();
    builder.Services.AddHostedService<AppLogWriterService>();
    builder.Services.AddHostedService<AppLogRetentionService>();
}


// Modular Processing Pipeline
builder.Services.AddDamebooruProcessing(damebooruConfig);

var app = builder.Build();

// Auto-apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
    var pendingMigrations = db.Database.GetPendingMigrations().ToList();
    if (pendingMigrations.Count > 0)
    {
        var sqliteDbPath = db.Database.GetDbConnection().DataSource;
        if (!string.IsNullOrWhiteSpace(sqliteDbPath) && File.Exists(sqliteDbPath))
        {
            var backupPath = sqliteDbPath + ".bak-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(sqliteDbPath, backupPath, overwrite: false);
            app.Logger.LogInformation("Backed up SQLite database before applying migrations: {BackupPath}", backupPath);
        }

        app.Logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pendingMigrations.Count, string.Join(", ", pendingMigrations));
    }

    db.Database.Migrate();

    // Reconcile stale "Running" executions left behind by shutdown/crash.
    var staleRunningExecutions = db.JobExecutions
        .Where(j => j.Status == JobStatus.Running && j.EndTime == null)
        .ToList();

    if (staleRunningExecutions.Count > 0)
    {
        var now = DateTime.UtcNow;
        foreach (var execution in staleRunningExecutions)
        {
            execution.Status = JobStatus.Cancelled;
            execution.EndTime = now;
            execution.ErrorMessage ??= "Marked as cancelled after server restart.";
        }

        db.SaveChanges();
        app.Logger.LogWarning("Reconciled {Count} stale running job execution(s) on startup.", staleRunningExecutions.Count);
    }
}

app.UseCors("AllowAngular");

if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseRateLimiter();

if (authEnabled)
{
    app.UseAuthentication();
}

var thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
    builder.Environment.ContentRootPath,
    damebooruConfig.Storage.ThumbnailPath);
if (!Directory.Exists(thumbnailPath))
{
    Directory.CreateDirectory(thumbnailPath);
}

app.Logger.LogInformation("Serving thumbnails from: {Path}", thumbnailPath);

if (authEnabled)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(MediaPaths.ThumbnailsRequestPath)
            && !(context.User.Identity?.IsAuthenticated ?? false))
        {
            await context.ChallengeAsync();
            return;
        }

        await next();
    });
}

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(thumbnailPath),
    RequestPath = MediaPaths.ThumbnailsRequestPath
});

app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => Results.Text("Damebooru API"));

app.Run();

static void TrimSecretLikeValues(DamebooruConfig config)
{
    config.Auth.Username = config.Auth.Username?.Trim() ?? string.Empty;
    config.Auth.Password = config.Auth.Password?.Trim() ?? string.Empty;

    config.ExternalApis.SauceNao.ApiKey = config.ExternalApis.SauceNao.ApiKey?.Trim() ?? string.Empty;
    config.ExternalApis.Danbooru.Username = config.ExternalApis.Danbooru.Username?.Trim() ?? string.Empty;
    config.ExternalApis.Danbooru.ApiKey = config.ExternalApis.Danbooru.ApiKey?.Trim() ?? string.Empty;
    config.ExternalApis.Gelbooru.UserId = config.ExternalApis.Gelbooru.UserId?.Trim() ?? string.Empty;
    config.ExternalApis.Gelbooru.ApiKey = config.ExternalApis.Gelbooru.ApiKey?.Trim() ?? string.Empty;
}
