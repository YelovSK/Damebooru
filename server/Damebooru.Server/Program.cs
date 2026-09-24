using Damebooru.Core.Config;
using Damebooru.Core.Interfaces;
using Damebooru.Core.Paths;
using Damebooru.Data;
using Damebooru.Processing;
using Damebooru.Processing.Services;
using Damebooru.Server.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authorization;
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

builder.Services.AddControllers();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(StoragePathResolver.ResolvePath(
        builder.Environment.ContentRootPath,
        damebooruConfig.Storage.DataProtectionKeysPath,
        "data/keys")));

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
                    if (IsApiOrGeneratedImagePath(context.Request.Path))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (IsApiOrGeneratedImagePath(context.Request.Path))
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
            var backupPath = SqliteDatabaseBackup.CreatePreMigrationBackup(db, sqliteDbPath);
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

var previewPath = MediaPaths.ResolvePreviewStoragePath(
    builder.Environment.ContentRootPath,
    damebooruConfig.Storage.PreviewPath);
if (!Directory.Exists(previewPath))
{
    Directory.CreateDirectory(previewPath);
}

var thumbnailPath = MediaPaths.ResolveThumbnailStoragePath(
    builder.Environment.ContentRootPath,
    damebooruConfig.Storage.ThumbnailPath);
if (!Directory.Exists(thumbnailPath))
{
    Directory.CreateDirectory(thumbnailPath);
}

GeneratedImageLayoutMigration.Run(previewPath, thumbnailPath, app.Logger);

app.Logger.LogInformation("Serving previews from: {Path}", previewPath);
app.Logger.LogInformation("Serving thumbnails from: {Path}", thumbnailPath);

if (authEnabled)
{
    app.Use(async (context, next) =>
    {
        if (IsGeneratedImagePath(context.Request.Path)
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
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(previewPath),
    RequestPath = MediaPaths.PreviewsRequestPath
});

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

static bool IsApiOrGeneratedImagePath(PathString path)
    => path.StartsWithSegments("/api") || IsGeneratedImagePath(path);

static bool IsGeneratedImagePath(PathString path)
    => path.StartsWithSegments(MediaPaths.PreviewsRequestPath)
        || path.StartsWithSegments(MediaPaths.ThumbnailsRequestPath);
