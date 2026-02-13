using Bakabooru.Core.Config;
using Bakabooru.Core.Paths;
using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Bakabooru.Processing;
using Bakabooru.Processing.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var bakabooruConfig = builder.Configuration.GetSection(BakabooruConfig.SectionName).Get<BakabooruConfig>() ?? new BakabooruConfig();
var authEnabled = bakabooruConfig.Auth.Enabled;

builder.Services.Configure<BakabooruConfig>(builder.Configuration.GetSection(BakabooruConfig.SectionName));

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

if (authEnabled)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "bakabooru_auth";
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
    bakabooruConfig.Storage.DatabasePath);

builder.Services.AddDbContext<BakabooruDbContext>(options =>
    options.UseSqlite(resolvedConnectionString));
builder.Services.AddScoped<PostReadService>();
builder.Services.AddScoped<PostWriteService>();
builder.Services.AddScoped<PostContentService>();
builder.Services.AddScoped<LibraryService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<TagCategoryService>();
builder.Services.AddScoped<DuplicateService>();
builder.Services.AddScoped<JobScheduleService>();
builder.Services.AddScoped<SystemReadService>();


// Modular Processing Pipeline
builder.Services.AddBakabooruProcessing(bakabooruConfig);

var app = builder.Build();

// Auto-apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BakabooruDbContext>();
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");

if (authEnabled)
{
    app.UseAuthentication();
}

var thumbnailPath = StoragePathResolver.ResolvePath(
    builder.Environment.ContentRootPath,
    bakabooruConfig.Storage.ThumbnailPath,
    "../../data/thumbnails");
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
                var requestedFile = Path.GetFileName(context.Request.Path.Value ?? string.Empty);
                var filePath = Path.Combine(thumbnailPath, requestedFile);
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

// Redirect root to swagger
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();
