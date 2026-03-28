using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;

public class UserAccount
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public bool EmailConfirmed { get; set; }

    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Staff";   // "Admin" | "Staff"
    public int? StaffId { get; set; }             // optional link to StaffMember

    public string? NotifyEmail { get; set; }
    public bool NotifyVoiceEnabled { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
    public bool IsDisabled { get; set; }

    // Dev-only email verify token (real email later)
    public string? EmailVerifyTokenHash { get; set; }
    public DateTime? EmailVerifyTokenExpiresUtc { get; set; }
}

public static class Accounts
{
    const string CookieName = "ceo_jwt";

    // DTOs (top-level, not local)
    public record AccountSummary(
    int Id, string Email, string Role, bool EmailConfirmed, bool IsDisabled,
    int? StaffId, DateTime CreatedUtc, DateTime? LastLoginUtc);

    public record AccountListItem(
    int Id, string Email, string Role, bool EmailConfirmed, bool IsDisabled,
    int? StaffId, string? StaffName, DateTime CreatedUtc, DateTime? LastLoginUtc);
    public record LinkStaffDto(int? StaffId);
    public record SetRoleDto(string Role);
    public record SetDisabledDto(bool Disabled);
    public record RegisterDto(string Email, string Password, string? Role, int? StaffId);
    public record LoginDto(string Email, string Password);

    public static IServiceCollection AddCeobotAccounts(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddScoped<PasswordHasher<UserAccount>>();

        var issuer = cfg["Jwt:Issuer"] ?? "CEObot";
        var audience = cfg["Jwt:Audience"] ?? "CEObot";
        var signingKey = cfg["Jwt:SigningKey"] ?? "dev-only-change-me-32chars-min";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

        // Avoid any automatic claim remapping; use our types exactly.
        JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

        services.AddAuthentication(o =>
        {
            o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(o =>
        {
            o.RequireHttpsMetadata = false; // true in prod
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),

                // 👇 ensure [Authorize]/RequireRole reads the same claim we wrote
                RoleClaimType = ClaimTypes.Role,
                NameClaimType = ClaimTypes.NameIdentifier
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    // allow JWT from our HttpOnly cookie
                    if (string.IsNullOrWhiteSpace(ctx.Token) &&
                        ctx.Request.Cookies.TryGetValue("ceo_jwt", out var jwt))
                    {
                        ctx.Token = jwt;
                    }
                    return Task.CompletedTask;
                }
            };
        });


        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("Admin", p => p.RequireRole("Admin"));
            opts.AddPolicy("Staff", p => p.RequireRole("Staff"));
        });

        return services;
    }


    public static IEndpointRouteBuilder MapCeobotAccounts(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        grp.MapPost("/register", async (
            RegisterDto dto,
            AppDb db,
            PasswordHasher<UserAccount> hasher,
            IConfiguration cfg,
            IWebHostEnvironment env,
            IEmailSender emailSender,
            ILoggerFactory loggerFactory,
            HttpRequest req) =>
        {
            var email = (dto.Email ?? "").Trim();
            var pwd = dto.Password ?? "";
            if (email.Length < 3 || pwd.Length < 8)
                return Results.BadRequest(new { error = "invalid email or password too short" });

            var norm = NormalizeEmail(email);
            var exists = await db.UserAccounts.AnyAsync(x => x.NormalizedEmail == norm);
            if (exists) return Results.Conflict(new { error = "email already registered" });

            var role = string.IsNullOrWhiteSpace(dto.Role) ? "Staff" : dto.Role!.Trim();
            if (role != "Admin" && role != "Staff") role = "Staff";

            var acc = new UserAccount
            {
                Email = email,
                NormalizedEmail = norm,
                Role = role,
                StaffId = dto.StaffId,
                EmailConfirmed = false
            };
            acc.PasswordHash = hasher.HashPassword(acc, pwd);

            // DEV verification token (real email later)
            var tokenPlain = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            acc.EmailVerifyTokenHash = HashToken(tokenPlain);
            acc.EmailVerifyTokenExpiresUtc = DateTime.UtcNow.AddHours(2);

            db.UserAccounts.Add(acc);
            await db.SaveChangesAsync();

            var devHint = env.IsDevelopment()
                ? new { verifyCodeDev = tokenPlain, verifyUrlDev = $"/api/auth/verify?email={Uri.EscapeDataString(email)}&code={tokenPlain}" }
                : null;

            try
            {
                var publicBase = cfg["PublicBaseUrl"]?.TrimEnd('/');
                var baseUrl = string.IsNullOrWhiteSpace(publicBase)
                    ? $"{req.Scheme}://{req.Host.Value}"
                    : publicBase;

                var verifyUrl = $"{baseUrl}/api/auth/verify?email={Uri.EscapeDataString(email)}&code={tokenPlain}";
                var body = new StringBuilder()
                    .AppendLine("Welcome to CEObot!")
                    .AppendLine()
                    .AppendLine("To confirm your account, click the link below or paste the code in the login page:")
                    .AppendLine(verifyUrl)
                    .AppendLine()
                    .AppendLine($"Verification code: {tokenPlain}")
                    .AppendLine()
                    .AppendLine("This link expires in 2 hours. If you didn't request this, you can ignore this email.")
                    .AppendLine()
                    .AppendLine("— CEObot")
                    .ToString();

                await emailSender.SendAsync(new EmailMessage(
                    email,
                    "Confirm your CEObot account",
                    body));
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("Accounts").LogError(ex, "Failed to send verification email to {Email}", email);
            }

            return Results.Ok(new { ok = true, role = acc.Role, dev = devHint });
        });

        // GET so dev link works directly
        grp.MapGet("/verify", async (string email, string code, AppDb db) =>
        {
            var norm = NormalizeEmail(email);
            var acc = await db.UserAccounts.SingleOrDefaultAsync(x => x.NormalizedEmail == norm);
            if (acc == null) return Results.NotFound(new { error = "no such account" });
            if (acc.EmailConfirmed) return Results.Ok(new { ok = true, already = true });
            if (acc.EmailVerifyTokenExpiresUtc < DateTime.UtcNow || string.IsNullOrEmpty(code))
                return Results.BadRequest(new { error = "code expired/invalid" });
            if (acc.EmailVerifyTokenHash != HashToken(code))
                return Results.BadRequest(new { error = "code mismatch" });

            acc.EmailConfirmed = true;
            acc.EmailVerifyTokenHash = null;
            acc.EmailVerifyTokenExpiresUtc = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        grp.MapPost("/login", async (
            LoginDto dto,
            AppDb db,
            PasswordHasher<UserAccount> hasher,
            IConfiguration cfg,
            HttpContext http) =>
        {
            var norm = NormalizeEmail(dto.Email);
            var acc = await db.UserAccounts.SingleOrDefaultAsync(x => x.NormalizedEmail == norm);
            if (acc == null || acc.IsDisabled)
                return Results.Unauthorized();

            var vr = hasher.VerifyHashedPassword(acc, acc.PasswordHash, dto.Password ?? "");
            if (vr == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            if (!acc.EmailConfirmed)
                return Results.BadRequest(new { error = "email not verified" });

            acc.LastLoginUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var jwt = IssueJwt(acc, cfg);
            WriteAuthCookie(http, jwt);

            return Results.Ok(new { ok = true, id = acc.Id, email = acc.Email, role = acc.Role, staffId = acc.StaffId });
        });

        grp.MapGet("/me", [Authorize] (ClaimsPrincipal user) =>
        {
            return Results.Ok(new
            {
                id = user.FindFirstValue(ClaimTypes.NameIdentifier),
                email = user.FindFirstValue(ClaimTypes.Email),
                role = user.FindFirstValue(ClaimTypes.Role)
            });
        });

        grp.MapGet("/prefs", [Authorize] async (ClaimsPrincipal user, AppDb db) =>
        {
            if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                return Results.Unauthorized();
            }

            var prefs = await db.UserAccounts.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => new
                {
                    notifyEmail = a.NotifyEmail,
                    notifyVoiceEnabled = a.NotifyVoiceEnabled
                })
                .SingleOrDefaultAsync();

            if (prefs is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(prefs);
        });

        grp.MapPost("/prefs", [Authorize] async (JsonElement body, ClaimsPrincipal user, AppDb db) =>
        {
            if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            {
                return Results.Unauthorized();
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { error = "invalid body" });
            }

            var acc = await db.UserAccounts.SingleOrDefaultAsync(a => a.Id == id);
            if (acc is null)
            {
                return Results.NotFound();
            }

            bool hasEmail = false;
            string? newEmail = null;
            if (body.TryGetProperty("notifyEmail", out var emailProp))
            {
                hasEmail = true;
                if (emailProp.ValueKind == JsonValueKind.Null)
                {
                    newEmail = null;
                }
                else if (emailProp.ValueKind == JsonValueKind.String)
                {
                    newEmail = emailProp.GetString();
                }
                else
                {
                    return Results.BadRequest(new { error = "notifyEmail must be string or null" });
                }
            }

            bool hasVoiceEnabled = false;
            bool? voiceEnabled = null;
            if (body.TryGetProperty("notifyVoiceEnabled", out var voiceProp))
            {
                hasVoiceEnabled = true;
                voiceEnabled = voiceProp.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => null
                };
                if (voiceEnabled is null && voiceProp.ValueKind is not JsonValueKind.Null)
                {
                    return Results.BadRequest(new { error = "notifyVoiceEnabled must be boolean" });
                }
            }

            if (hasEmail)
            {
                var trimmed = string.IsNullOrWhiteSpace(newEmail) ? null : newEmail!.Trim();
                if (trimmed is not null && !EmailValidation.IsValid(trimmed))
                {
                    return Results.BadRequest(new { error = "invalid email" });
                }

                acc.NotifyEmail = trimmed;
                if (trimmed is null)
                {
                    acc.NotifyVoiceEnabled = false;
                }
            }

            if (hasVoiceEnabled && voiceEnabled.HasValue)
            {
                if (voiceEnabled.Value && string.IsNullOrWhiteSpace(acc.NotifyEmail))
                {
                    return Results.BadRequest(new { error = "notifyEmail must be set before enabling voice notifications" });
                }

                acc.NotifyVoiceEnabled = voiceEnabled.Value;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                notifyEmail = acc.NotifyEmail,
                notifyVoiceEnabled = acc.NotifyVoiceEnabled
            });
        });

        grp.MapPost("/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(CookieName, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/auth/claims", (HttpContext ctx) =>
        {
            var u = ctx.User;
            return Results.Ok(new
            {
                isAuth = u?.Identity?.IsAuthenticated ?? false,
                name = u?.Identity?.Name,
                roles = u?.Claims
                    .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                    .Select(c => c.Value)
                    .ToArray(),
                all = u?.Claims.Select(c => new { c.Type, c.Value }).ToArray()
            });
        });


        // ========== ADMIN: minimal account management (link/unlink to Staff) ==========
        var adminAcc = app.MapGroup("/api/admin/accounts").RequireAuthorization("Admin");

        // List accounts with paging and search
        adminAcc.MapGet("", async (string? search, int skip, int take, AppDb db) =>
        {
            if (take <= 0 || take > 200) take = 50;
            if (skip < 0) skip = 0;
            search = (search ?? "").Trim();

            var q = db.UserAccounts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.ToUpperInvariant();
                q = q.Where(a => a.NormalizedEmail.Contains(s));
            }

            var items = await q
                .OrderBy(a => a.Id)
                .Skip(skip).Take(take)
                .Select(a => new AccountListItem(
                    a.Id, a.Email, a.Role, a.EmailConfirmed, a.IsDisabled,
                    a.StaffId,
                    db.Staff.Where(s => s.Id == a.StaffId).Select(s => s.Name).FirstOrDefault(),
                    a.CreatedUtc, a.LastLoginUtc
                ))
                .ToListAsync();

            var total = await q.CountAsync();
            return Results.Ok(new { total, items });
        });

        // Change role (Admin | Staff)
        adminAcc.MapPost("/{id:int}/role", async (int id, SetRoleDto dto, AppDb db) =>
        {
            var role = (dto.Role ?? "").Trim();
            if (role != "Admin" && role != "Staff")
                return Results.BadRequest(new { error = "role must be Admin or Staff" });

            var acc = await db.UserAccounts.FindAsync(id);
            if (acc is null) return Results.NotFound();

            acc.Role = role;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, id = acc.Id, role = acc.Role });
        });

        // Enable/Disable account
        adminAcc.MapPost("/{id:int}/disabled", async (int id, SetDisabledDto dto, AppDb db) =>
        {
            var acc = await db.UserAccounts.FindAsync(id);
            if (acc is null) return Results.NotFound();

            acc.IsDisabled = dto.Disabled;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, id = acc.Id, disabled = acc.IsDisabled });
        });

        // (Optional) Delete account
        adminAcc.MapDelete("/{id:int}", async (int id, AppDb db) =>
        {
            var acc = await db.UserAccounts.FindAsync(id);
            if (acc is null) return Results.NotFound();
            db.UserAccounts.Remove(acc);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        });

        adminAcc.MapGet("/{id:int}", async (int id, AppDb db) =>
        {
            var a = await db.UserAccounts.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new AccountSummary(
                    x.Id, x.Email, x.Role, x.EmailConfirmed, x.IsDisabled,
                    x.StaffId, x.CreatedUtc, x.LastLoginUtc))
                .SingleOrDefaultAsync();

            return a is null ? Results.NotFound() : Results.Ok(a);
        });

        adminAcc.MapPost("/{id:int}/link-staff", async (int id, LinkStaffDto dto, AppDb db) =>
        {
            var acc = await db.UserAccounts.FindAsync(id);
            if (acc is null) return Results.NotFound(new { error = "account not found" });

            if (dto.StaffId is not null)
            {
                var exists = await db.Staff.AnyAsync(s => s.Id == dto.StaffId.Value);
                if (!exists) return Results.BadRequest(new { error = "staffId not found" });
            }

            acc.StaffId = dto.StaffId; // null => unlink
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, accountId = acc.Id, staffId = acc.StaffId });
        });
        // ============================================================================

        return app;
    }


    // ===== helpers =====
    static string NormalizeEmail(string e) => (e ?? "").Trim().ToUpperInvariant();

    static string IssueJwt(UserAccount acc, IConfiguration cfg)
    {
        var issuer = cfg["Jwt:Issuer"] ?? "CEObot";
        var audience = cfg["Jwt:Audience"] ?? "CEObot";
        var signingKey = cfg["Jwt:SigningKey"] ?? "dev-only-change-me-32chars-min";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, acc.Id.ToString()),
            new Claim(ClaimTypes.Email, acc.Email),
            new Claim(ClaimTypes.Role, acc.Role)
            // If you later want self-scope on staff endpoints, add:
            // acc.StaffId is int sid ? new Claim("staffId", sid.ToString()) : null
        };

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    static void WriteAuthCookie(HttpContext http, string jwt)
    {
        http.Response.Cookies.Append(CookieName, jwt, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromMinutes(30),
            IsEssential = true,
            Path = "/"
        });
    }

    static string HashToken(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
