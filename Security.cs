// Security.cs
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class SecurityExtensions
{
    public static IServiceCollection AddCeobotSecurity(this IServiceCollection services, IConfiguration cfg)
    {
        // 1) CORS (explicit origins only)
        var origins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        services.AddCors(o =>
        {
            o.AddPolicy("Frontend", b =>
            {
                if (origins.Length > 0)
                    b.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
                else
                    b.DisallowCredentials(); // same-origin only
            });
        });

        // 2) Forwarded headers
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear(); o.KnownProxies.Clear();
        });

        // 3) Rate limiting
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });

            options.AddPolicy("voice", ctx =>
            {
                var key = (ctx.Connection.RemoteIpAddress?.ToString() ?? "anon") + ":voice";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 12,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                });
            });
        });

        return services;
    }

    public static IApplicationBuilder UseCeobotSecurity(this IApplicationBuilder app, IConfiguration cfg, IHostEnvironment env)
    {
        // HTTPS + HSTS
        if (!env.IsDevelopment()) app.UseHsts();
        app.UseHttpsRedirection();

        // Proxy headers first
        app.UseForwardedHeaders();

        // CORS (enable if you set AllowedOrigins)
        app.UseCors("Frontend");

        // --- DEV ONLY: promote ?key= to X-Admin-Key so old tools still work locally
        if (env.IsDevelopment())
        {
            app.Use(async (ctx, next) =>
            {
                var qsKey = ctx.Request.Query["key"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(qsKey) && !ctx.Request.Headers.ContainsKey("X-Admin-Key"))
                    ctx.Request.Headers["X-Admin-Key"] = qsKey;
                await next();
            });
        }

        // ===== Admin gate (cookie OR header) for /api/diag and /api/admin =====
        // NOTE: app.UseAuthentication()/UseAuthorization() MUST run BEFORE this (see Program.cs).
        // ===== Admin gate (cookie OR header) for /api/diag and /api/admin =====
        app.Use(async (ctx, next) =>
        {
            bool isAdminArea =
                (ctx.Request.Path.StartsWithSegments("/api/diag") ||
                 ctx.Request.Path.StartsWithSegments("/api/admin"));

            if (isAdminArea && !HttpMethods.IsOptions(ctx.Request.Method))
            {
                // 👇 This relies on Authentication having run earlier
                bool isAdminCookie = ctx.User?.Identity?.IsAuthenticated == true &&
                                     ctx.User.IsInRole("Admin");

                if (!isAdminCookie)
                {
                    // Fallback: header key (and ?key= in Dev if you kept that shim)
                    var supplied = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault()?.Trim();
                    var configured = cfg["Admin:Key"]?.Trim();

                    if (string.IsNullOrEmpty(supplied) ||
                        string.IsNullOrEmpty(configured) ||
                        !string.Equals(supplied, configured, StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsJsonAsync(new { error = "admin auth required" });
                        return;
                    }
                }
            }

            await next();
        });


        // Security headers (for /api/*)
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.Headers["Cache-Control"] = "no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                ctx.Response.Headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(self)";
                ctx.Response.Headers["X-Frame-Options"] = "DENY";
                ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
                ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'self'";
            }
            await next();
        });

        // Rate limiting
        app.UseRateLimiter();

        return app;
    }
}
