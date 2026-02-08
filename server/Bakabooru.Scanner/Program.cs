using Bakabooru.Core.Interfaces;
using Bakabooru.Data;
using Bakabooru.Scanner;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<BakabooruDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<IHasherService, Md5Hasher>();
builder.Services.AddSingleton<IImageProcessor, ImageSharpProcessor>();
builder.Services.AddSingleton<ISimilarityService, ImageHashService>();
builder.Services.AddTransient<IScannerService, RecursiveScanner>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
