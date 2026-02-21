using Damebooru.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Damebooru.Data;

public class DamebooruDbContext : DbContext
{
    public DamebooruDbContext(DbContextOptions<DamebooruDbContext> options) : base(options)
    {
    }

    public DbSet<Library> Libraries { get; set; } = null!;
    public DbSet<Post> Posts { get; set; } = null!;
    public DbSet<Tag> Tags { get; set; } = null!;
    public DbSet<TagCategory> TagCategories { get; set; } = null!;
    public DbSet<PostTag> PostTags { get; set; } = null!;
    public DbSet<PostSource> PostSources { get; set; } = null!;

    public DbSet<JobExecution> JobExecutions { get; set; } = null!;
    public DbSet<ScheduledJob> ScheduledJobs { get; set; } = null!;

    public DbSet<ExcludedFile> ExcludedFiles { get; set; } = null!;
    public DbSet<LibraryIgnoredPath> LibraryIgnoredPaths { get; set; } = null!;
    public DbSet<DuplicateGroup> DuplicateGroups { get; set; } = null!;
    public DbSet<DuplicateGroupEntry> DuplicateGroupEntries { get; set; } = null!;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>()
            .HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure PostTag composite key
        modelBuilder.Entity<PostTag>()
            .HasKey(pt => new { pt.PostId, pt.TagId, pt.Source });

        modelBuilder.Entity<PostTag>()
            .HasOne(pt => pt.Post)
            .WithMany(p => p.PostTags)
            .HasForeignKey(pt => pt.PostId);

        modelBuilder.Entity<PostTag>()
            .HasOne(pt => pt.Tag)
            .WithMany(t => t.PostTags)
            .HasForeignKey(pt => pt.TagId);

        modelBuilder.Entity<PostSource>()
            .HasOne(ps => ps.Post)
            .WithMany(p => p.Sources)
            .HasForeignKey(ps => ps.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Library -> Post relationship
        modelBuilder.Entity<Post>()
            .HasOne(p => p.Library)
            .WithMany()
            .HasForeignKey(p => p.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);
            
         // Configure Tag -> TagCategory relationship
         modelBuilder.Entity<Tag>()
            .HasOne(t => t.TagCategory)
            .WithMany(c => c.Tags)
            .HasForeignKey(t => t.TagCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<JobExecution>()
            .HasIndex(j => j.JobName);
            
        modelBuilder.Entity<JobExecution>()
            .HasIndex(j => j.StartTime);

        // Indexes for performance
        modelBuilder.Entity<Post>()
            .HasIndex(p => p.ContentHash);

        modelBuilder.Entity<Post>()
            .HasIndex(p => new { p.ImportDate, p.Id });

        modelBuilder.Entity<Post>()
            .HasIndex(p => new { p.FileModifiedDate, p.Id });

        modelBuilder.Entity<Post>()
            .HasIndex(p => new { p.LibraryId, p.RelativePath });

        modelBuilder.Entity<Post>()
            .HasIndex(p => new { p.LibraryId, p.FileIdentityDevice, p.FileIdentityValue });

        modelBuilder.Entity<Post>()
            .HasIndex(p => p.IsFavorite);

        modelBuilder.Entity<PostTag>()
            .HasIndex(p => new { p.TagId, p.PostId, p.Source });

        modelBuilder.Entity<PostTag>()
            .HasIndex(p => new { p.PostId, p.Source });

        modelBuilder.Entity<PostSource>()
            .HasIndex(ps => new { ps.PostId, ps.Order })
            .IsUnique();

        modelBuilder.Entity<Tag>()
            .HasIndex(t => t.Name)
            .IsUnique();

        // Duplicate detection
        modelBuilder.Entity<DuplicateGroupEntry>()
            .HasOne(e => e.DuplicateGroup)
            .WithMany(g => g.Entries)
            .HasForeignKey(e => e.DuplicateGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DuplicateGroupEntry>()
            .HasOne(e => e.Post)
            .WithMany(p => p.DuplicateGroupEntries)
            .HasForeignKey(e => e.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DuplicateGroup>()
            .HasIndex(g => g.IsResolved);

        // Exclusion list
        modelBuilder.Entity<ExcludedFile>()
            .HasOne(e => e.Library)
            .WithMany()
            .HasForeignKey(e => e.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExcludedFile>()
            .HasIndex(e => new { e.LibraryId, e.RelativePath })
            .IsUnique();

        modelBuilder.Entity<LibraryIgnoredPath>()
            .HasOne(p => p.Library)
            .WithMany()
            .HasForeignKey(p => p.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LibraryIgnoredPath>()
            .HasIndex(p => new { p.LibraryId, p.RelativePathPrefix })
            .IsUnique();
    }
}
