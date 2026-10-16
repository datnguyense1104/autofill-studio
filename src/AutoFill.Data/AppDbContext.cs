using AutoFill.Core;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Data;

/// <summary>
/// All tables. Workspace-owned rows are filtered by the current workspace: a request that has
/// no workspace sees none of them (deny by default). Background code that must look across
/// workspaces (the job queue) says so with <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IWorkspaceScope scope)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunValue> RunValues => Set<RunValue>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AiLog> AiLogs => Set<AiLog>();
    /// <summary>Cookie encryption keys, kept in the database so logins survive container restarts.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Read by the query filters on every query (EF parameterizes it per context instance).</summary>
    public Guid? CurrentWorkspaceId => scope.WorkspaceId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(200);
            e.Property(u => u.Name).HasMaxLength(100);
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Workspace>(e => e.Property(w => w.Name).HasMaxLength(100));
        b.Entity<Membership>(e =>
        {
            e.HasKey(m => new { m.WorkspaceId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<StoredFile>(e =>
        {
            e.HasIndex(f => new { f.WorkspaceId, f.Sha256 });
            e.HasIndex(f => f.Key);
            e.Property(f => f.Key).HasMaxLength(200);
            e.Property(f => f.Name).HasMaxLength(300);
            e.Property(f => f.ContentType).HasMaxLength(120);
            e.Property(f => f.Sha256).HasMaxLength(64);
            e.HasOne<Workspace>().WithMany().HasForeignKey(f => f.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(f => f.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<Transcript>(e =>
        {
            e.HasIndex(t => new { t.WorkspaceId, t.Sha256 }).IsUnique();
            e.Property(t => t.Sha256).HasMaxLength(64);
            e.Property(t => t.Document).HasColumnType("jsonb");
            e.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(t => t.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<Recipe>(e =>
        {
            e.HasIndex(r => new { r.WorkspaceId, r.Name });
            e.Property(r => r.Name).HasMaxLength(200);
            e.Property(r => r.UseWhen).HasMaxLength(2000);
            e.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => r.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<RecipeVersion>(e =>
        {
            e.HasIndex(v => new { v.RecipeId, v.Number }).IsUnique();
            e.Property(v => v.Kind).HasMaxLength(10);
            e.Property(v => v.Slots).HasColumnType("jsonb");
            e.Property(v => v.Rules).HasColumnType("jsonb");
            e.HasOne<Recipe>().WithMany().HasForeignKey(v => v.RecipeId).OnDelete(DeleteBehavior.Cascade);
            // A template file stays as long as a version uses it.
            e.HasOne<StoredFile>().WithMany().HasForeignKey(v => v.TemplateFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(v => v.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<Run>(e =>
        {
            e.HasIndex(r => new { r.WorkspaceId, r.CreatedAt });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.SourceFileIds).HasColumnType("jsonb");
            e.Property(r => r.Missing).HasColumnType("jsonb");
            e.Property(r => r.Notes).HasColumnType("jsonb");
            e.HasOne<RecipeVersion>().WithMany().HasForeignKey(r => r.RecipeVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => r.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<RunValue>(e =>
        {
            e.HasKey(v => new { v.RunId, v.SlotId });
            e.Property(v => v.SlotId).HasMaxLength(60);
            e.Property(v => v.Origin).HasMaxLength(10);
            e.Property(v => v.Value).HasColumnType("jsonb");
            e.Property(v => v.Evidence).HasColumnType("jsonb");
            e.Property(v => v.Warnings).HasColumnType("jsonb");
            e.Property(v => v.ReviewedValue).HasColumnType("jsonb");
            e.HasOne<Run>().WithMany().HasForeignKey(v => v.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(v => v.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<Job>(e =>
        {
            e.HasIndex(j => new { j.Status, j.CreatedAt });
            e.Property(j => j.Type).HasMaxLength(60);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Payload).HasColumnType("jsonb");
            e.Property(j => j.Progress).HasColumnType("jsonb");
            e.Property(j => j.Result).HasColumnType("jsonb");
            e.HasOne<Workspace>().WithMany().HasForeignKey(j => j.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(j => j.WorkspaceId == CurrentWorkspaceId);
        });
        b.Entity<AiLog>(e =>
        {
            e.HasIndex(l => l.CreatedAt);
            e.HasIndex(l => l.JobId);
            e.Property(l => l.Purpose).HasMaxLength(60);
            e.Property(l => l.Model).HasMaxLength(60);
        });
    }

    /// <summary>New workspace rows get the current workspace; writing into another workspace is refused.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<IWorkspaceOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            if (entry.Entity.WorkspaceId == Guid.Empty)
                entry.Entity.WorkspaceId = scope.WorkspaceId ?? throw new InvalidOperationException("No workspace for a workspace-owned row");
            else if (scope.WorkspaceId is { } current && entry.Entity.WorkspaceId != current)
                throw new InvalidOperationException("Row belongs to another workspace");
        }
        return base.SaveChangesAsync(ct);
    }
}
