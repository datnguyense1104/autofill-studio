using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace AutoFill.Data;

public static class Database
{
    /// <summary>
    /// Accepts both the URI form Supabase shows (postgresql://user:pass@host:5432/postgres)
    /// and the Npgsql keyword form. Supabase hosts get TLS with full certificate checks.
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(string databaseUrl)
    {
        var csb = ToConnectionString(databaseUrl);
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        if (IsSupabase(csb.Host))
        {
            builder.UseRootCertificateCallback(() => X509Certificate2.CreateFromPem(SupabaseCa.Root2021));
        }
        return builder.Build();
    }

    public static NpgsqlConnectionStringBuilder ToConnectionString(string databaseUrl)
    {
        NpgsqlConnectionStringBuilder csb;
        if (databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(databaseUrl);
            var userInfo = uri.UserInfo.Split(':', 2);
            csb = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) is { Length: > 0 } db ? db : "postgres",
                Username = Uri.UnescapeDataString(userInfo[0]),
                Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            };
        }
        else
        {
            csb = new NpgsqlConnectionStringBuilder(databaseUrl);
        }
        if (IsSupabase(csb.Host))
        {
            csb.SslMode = SslMode.VerifyFull;
            // The transaction pooler (port 6543) cannot keep prepared statements between transactions.
            if (csb.Port == 6543) csb.MaxAutoPrepare = 0;
        }
        return csb;
    }

    static bool IsSupabase(string? host) =>
        host is not null && (host.EndsWith(".supabase.com", StringComparison.OrdinalIgnoreCase)
                             || host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies migrations, then turns on row level security for every table in the public schema
    /// (including tables added later). With no policies, Supabase's public Data API sees no rows;
    /// this server connects as the table owner, which RLS does not restrict.
    /// </summary>
    public static async Task MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            DECLARE t record;
            BEGIN
              FOR t IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND NOT rowsecurity LOOP
                EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t.tablename);
              END LOOP;
            END $$;
            """, ct);
    }

    public static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, o => o.MigrationsHistoryTable("ef_migrations"))
               .UseSnakeCaseNamingConvention();
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a running app or database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        Database.Configure(options, Database.CreateDataSource("Host=localhost;Database=design;Username=design"));
        return new AppDbContext(options.Options, new Core.WorkspaceScope());
    }
}
