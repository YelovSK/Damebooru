using Bakabooru.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bakabooru.Data;

public class BakabooruDbContext : DbContext
{
    public BakabooruDbContext(DbContextOptions<BakabooruDbContext> options) : base(options)
    {
    }

    public DbSet<Library> Libraries { get; set; } = null!;
    public DbSet<Post> Posts { get; set; } = null!;
    public DbSet<Tag> Tags { get; set; } = null!;
    public DbSet<TagCategory> TagCategories { get; set; } = null!;
    public DbSet<PostTag> PostTags { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure PostTag composite key
        modelBuilder.Entity<PostTag>()
            .HasKey(pt => new { pt.PostId, pt.TagId });

        modelBuilder.Entity<PostTag>()
            .HasOne(pt => pt.Post)
            .WithMany(p => p.PostTags)
            .HasForeignKey(pt => pt.PostId);

        modelBuilder.Entity<PostTag>()
            .HasOne(pt => pt.Tag)
            .WithMany(t => t.PostTags)
            .HasForeignKey(pt => pt.TagId);

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

        // Indexes for performance
        modelBuilder.Entity<Post>()
            .HasIndex(p => p.Md5Hash);
            
        modelBuilder.Entity<Tag>()
            .HasIndex(t => t.Name)
            .IsUnique();
    }
}
