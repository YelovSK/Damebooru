using Damebooru.Core.Config;
using Damebooru.Core.Paths;
using Damebooru.Core.Interfaces;
using Damebooru.Data;
using Damebooru.Processing;
using Damebooru.Processing.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var damebooruConfig = builder.Configuration.GetSection(DamebooruConfig.SectionName).Get<DamebooruConfig>() ?? new DamebooruConfig();
var authEnabled = damebooruConfig.Auth.Enabled;

builder.Services.Configure<DamebooruConfig>(builder.Configuration.GetSection(DamebooruConfig.SectionName));

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
            options.ExpireTimeSpan = TimeSpan.FromDays(30);

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

builder.Services.AddAuthorization(options =>
{
    if (authEnabled)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }
});

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

builder.Services.AddDbContext<DamebooruDbContext>(options =>
    options.UseSqlite(resolvedConnectionString));
builder.Services.AddScoped<PostReadService>();
builder.Services.AddScoped<PostWriteService>();
builder.Services.AddScoped<PostContentService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<LibraryBrowseService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<TagCategoryService>();
builder.Services.AddScoped<DuplicateService>();
builder.Services.AddScoped<DuplicateQueryService>();
builder.Services.AddScoped<DuplicateMutationSupportService>();
builder.Services.AddScoped<JobScheduleService>();
builder.Services.AddScoped<SystemReadService>();


// Modular Processing Pipeline
builder.Services.AddDamebooruProcessing(damebooruConfig);

var app = builder.Build();

// Auto-apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DamebooruDbContext>();
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

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(MediaPaths.ThumbnailsRequestPath))
        {
            await next();
            if (context.Response.StatusCode == 404)
            {
                var requestPath = context.Request.Path.Value ?? string.Empty;
                var relativePath = requestPath.StartsWith(MediaPaths.ThumbnailsRequestPath, StringComparison.OrdinalIgnoreCase)
                    ? requestPath[MediaPaths.ThumbnailsRequestPath.Length..].TrimStart('/')
                    : requestPath.TrimStart('/');
                var filePath = Path.Combine(thumbnailPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var exists = File.Exists(filePath);
                app.Logger.LogWarning("Thumbnail 404: {Url} (File exists at {Path}: {Exists})", context.Request.Path, filePath, exists);
            }
        }
        else
        {
            await next();
        }
    });
}

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
