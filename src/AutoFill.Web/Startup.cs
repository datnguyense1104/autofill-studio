using AutoFill.Ai;
using AutoFill.Core;
using AutoFill.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoFill.Documents.Ingest;
using AutoFill.Documents.Runs;
using AutoFill.Documents.Templates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AutoFill.Web;

public static class Startup
{
    public static IServiceCollection AddAutoFill(this IServiceCollection services, Settings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => Database.CreateDataSource(settings.DatabaseUrl));
        services.AddDbContext<AppDbContext>((sp, o) => Database.Configure(o, sp.GetRequiredService<Npgsql.NpgsqlDataSource>()));
        services.AddScoped<WorkspaceScope>();
        services.AddScoped<IWorkspaceScope>(sp => sp.GetRequiredService<WorkspaceScope>());

        if (settings.FileStore == "supabase")
        {
            var options = new SupabaseStorageOptions(settings.SupabaseUrl!, settings.SupabaseServiceKey!, settings.SupabaseBucket, settings.MaxFileBytes);
            services.AddHttpClient(nameof(SupabaseFileStore), c => c.Timeout = TimeSpan.FromMinutes(5));
            services.AddSingleton<IFileStore>(sp =>
                new SupabaseFileStore(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SupabaseFileStore)), options));
        }
        else
        {
            services.AddSingleton<IFileStore>(new DiskFileStore(Path.Combine(settings.DataDir, "files")));
        }
        services.AddSingleton(new FileLimits(settings.MaxFileBytes));
        services.AddScoped<FileService>();

        services.AddSingleton(new JobQueueOptions(Lease: TimeSpan.FromMinutes(10), MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(15)));
        services.AddScoped<JobQueue>();
        services.AddSingleton<JobWorker>();
        if (settings.Workers > 0) services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());

        services.AddSingleton(new AiOptions(settings.OpenAiApiKey, settings.OpenAiModel, settings.OpenAiBaseUrl));
        services.AddSingleton<IAiUsageLog, AiUsageLog>();
        services.AddScoped<IAiClient, OpenAiJsonClient>();

        services.AddSingleton(new LegacyConverter(new LegacyConverterOptions(settings.SofficePath, TimeSpan.FromSeconds(90))));
        services.AddScoped<ScanTranscriber>();
        services.AddScoped<SourceReader>();
        services.AddScoped<SourceService>();
        services.AddScoped<IJobHandler, SourceReadJob>();
        services.AddScoped<TemplateAnalyzer>();
        services.AddScoped<IJobHandler, TemplateAnalyzeJob>();
        services.AddSingleton(new RunOptions(MaxSourceChars: settings.MaxSourceChars));
        services.AddScoped<RunEngine>();
        services.AddScoped<RecipeSuggester>();
        services.AddScoped<RunService>();
        services.AddScoped<IJobHandler, RunJob>();
        services.AddScoped<IJobHandler, RecipeSuggestJob>();
        // Slots travel as JSON both ways: enums as names, locations tagged by "kind".
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            o.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
        });

        services.AddScoped<Accounts>();
        services.AddDataProtection().SetApplicationName("autofill-studio").PersistKeysToDbContext<AppDbContext>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "autofill.session";
            // Other apps on the same domain never receive the login cookie.
            o.Cookie.Path = settings.BasePath.Length > 0 ? settings.BasePath : "/";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = settings.CookieSecure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromDays(14);
            o.SlidingExpiration = true;
            // An API answers 401/403; the page script sends the user to the login page.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        services.AddAuthorization();
        return services;
    }

    public static async Task MigrateDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await Database.MigrateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public static void UseAutoFill(this WebApplication app, Settings settings)
    {
        if (settings.BasePath.Length > 0)
        {
            app.UsePathBase(settings.BasePath);
            app.Use(async (ctx, next) =>
            {
                // "/auto-fill" → "/auto-fill/": the pages use relative links, which need the trailing slash.
                if (ctx.Request.PathBase.HasValue && !ctx.Request.Path.HasValue)
                {
                    ctx.Response.Redirect($"{ctx.Request.PathBase}/{ctx.Request.QueryString}");
                    return;
                }
                // Outside the prefix the login cookie (scoped to the prefix) is never sent: send pages into it.
                if (!ctx.Request.PathBase.HasValue)
                {
                    if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                        ctx.Response.Redirect($"{settings.BasePath}{ctx.Request.Path}{ctx.Request.QueryString}");
                    else
                        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });
        }
        app.Use(SecurityHeaders(settings));
        app.Use(UserErrors);
        app.UseDefaultFiles();
        // Pages and their modules must change together: without this, Cloudflare and browsers keep
        // .js/.css for hours and a new index.html meets old scripts after a deploy. Unchanged files
        // still cost only a 304.
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache",
        });
        app.UseAuthentication();
        app.Use(CurrentUser.Resolve);
        app.UseAuthorization();

        app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { ok = true }) : Results.StatusCode(503));
        app.MapAuth();
        app.MapWorkspaces();
        app.MapUploads();
        app.MapSources();
        app.MapTemplates();
        app.MapRecipes();
        app.MapRuns();
        app.MapJobs();
        app.MapAdmin();
        app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "Không có API này" }));
    }

    /// <summary>Errors meant for the user come back as JSON {error}; anything else is logged and hidden.</summary>
    static async Task UserErrors(HttpContext ctx, Func<Task> next)
    {
        try
        {
            await next();
        }
        catch (UserFacingException err) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = err.Status;
            await ctx.Response.WriteAsJsonAsync(new { error = err.Message });
        }
        catch (BadHttpRequestException err) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = err.StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { error = "Yêu cầu không hợp lệ" });
        }
        catch (Exception err) when (!ctx.Response.HasStarted && err is not OperationCanceledException)
        {
            ctx.RequestServices.GetRequiredService<ILogger<Settings>>().LogError(err, "Unhandled error on {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = "Lỗi máy chủ, hãy thử lại sau" });
        }
    }

    static Func<HttpContext, Func<Task>, Task> SecurityHeaders(Settings settings)
    {
        // Direct uploads PUT to Supabase Storage from the page.
        var connect = settings.SupabaseUrl is { } url ? $"'self' {new Uri(url).GetLeftPart(UriPartial.Authority)}" : "'self'";
        var csp = $"default-src 'self'; connect-src {connect}; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        return async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.ContentSecurityPolicy = csp;
            h.XContentTypeOptions = "nosniff";
            h["Referrer-Policy"] = "same-origin";
            h.XFrameOptions = "DENY";
            await next();
        };
    }
}
