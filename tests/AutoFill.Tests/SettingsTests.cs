using AutoFill.Data;
using AutoFill.Web;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AutoFill.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Supabase_uri_becomes_a_verified_tls_connection()
    {
        var csb = Database.ToConnectionString("postgresql://postgres.abcd:p%40ss%2Fword@aws-1-ap-southeast-1.pooler.supabase.com:5432/postgres");
        Assert.Equal("aws-1-ap-southeast-1.pooler.supabase.com", csb.Host);
        Assert.Equal(5432, csb.Port);
        Assert.Equal("postgres.abcd", csb.Username);
        Assert.Equal("p@ss/word", csb.Password);
        Assert.Equal("postgres", csb.Database);
        Assert.Equal(SslMode.VerifyFull, csb.SslMode);
    }

    [Fact]
    public void Transaction_pooler_turns_off_prepared_statements_and_plain_hosts_are_left_alone()
    {
        Assert.Equal(0, Database.ToConnectionString("postgres://u:p@x.pooler.supabase.com:6543/postgres").MaxAutoPrepare);
        var local = Database.ToConnectionString("Host=localhost;Port=5433;Username=postgres;Database=dev");
        Assert.Equal(SslMode.Prefer, local.SslMode);
        Assert.Equal(5433, local.Port);
    }

    [Fact]
    public void Supabase_root_certificate_is_valid()
    {
        using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(SupabaseCa.Root2021);
        Assert.Contains("Supabase Root 2021 CA", cert.Subject);
    }

    static Settings Build(Dictionary<string, string?> values, bool dev = false) =>
        Settings.From(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), dev);

    [Fact]
    public void File_store_follows_supabase_settings()
    {
        Assert.Equal("disk", Build(new() { ["DATABASE_URL"] = "Host=x" }).FileStore);
        var s = Build(new() { ["DATABASE_URL"] = "Host=x", ["SUPABASE_URL"] = "https://a.supabase.co", ["SUPABASE_SERVICE_KEY"] = "k" });
        Assert.Equal("supabase", s.FileStore);
        Assert.Equal("autofill", s.SupabaseBucket);
        Assert.True(s.CookieSecure);
        Assert.Throws<InvalidOperationException>(() => Build(new() { ["DATABASE_URL"] = "Host=x", ["FILE_STORE"] = "supabase" }));
        Assert.Throws<InvalidOperationException>(() => Build(new()));
    }

    [Fact]
    public void Env_file_fills_missing_variables_only()
    {
        var path = Path.GetTempFileName();
        var unique = $"AUTOFILL_TEST_{Guid.NewGuid():N}";
        File.WriteAllLines(path, ["# comment", $"{unique}_A=\"quoted value\"", $"export {unique}_B=plain", $"{unique}_C=from-file", "broken line"]);
        Environment.SetEnvironmentVariable($"{unique}_C", "from-process");
        try
        {
            EnvFile.Load(path);
            Assert.Equal("quoted value", Environment.GetEnvironmentVariable($"{unique}_A"));
            Assert.Equal("plain", Environment.GetEnvironmentVariable($"{unique}_B"));
            Assert.Equal("from-process", Environment.GetEnvironmentVariable($"{unique}_C"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ws/0123456789abcdef0123456789abcdef/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.pdf", true)]
    [InlineData("uploads/0123456789abcdef0123456789abcdef/0123456789abcdef0123456789abcdef.xlsx", true)]
    [InlineData("uploads/0123456789abcdef0123456789abcdef/../secret.pdf", false)]
    [InlineData("ws/0123456789abcdef0123456789abcdef/abc.exe", false)]
    public void File_keys_are_strict(string key, bool valid) => Assert.Equal(valid, FileKeys.IsValid(key));
}
