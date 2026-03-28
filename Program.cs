// ===== BLOCK: USINGS START =====
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
// ===== BLOCK: USINGS END =====

// ===== BLOCK: HOST_BUILDER START =====
var builder = WebApplication.CreateBuilder(args);
// ===== BLOCK: HOST_BUILDER END =====

// ===== BLOCK: DB_CONFIG START =====
// DB
builder.Services.AddDbContext<AppDb>(opt =>
{
    var cs = builder.Configuration.GetConnectionString("db") ?? "Data Source=ceobot.db";
    opt.UseSqlite(cs);
});
// ===== BLOCK: DB_CONFIG END =====

// ===== BLOCK: JSON_CONFIG START =====
// JSON
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
// ===== BLOCK: JSON_CONFIG END =====

// ===== BLOCK: AI_CLIENT_CONFIG START =====
// AI client
builder.Services.AddHttpClient<IAIClassifier, OpenAiClassifier>(c => c.Timeout = TimeSpan.FromSeconds(30));
// NEW: speech-to-text transcriber (for /api/voice)
builder.Services.AddHttpClient<IAudioTranscriber, OpenAIWhisperTranscriber>(c => c.Timeout = TimeSpan.FromMinutes(2));
// NEW: transcript parser (AI-first, regex fallback)
builder.Services.AddHttpClient<ITranscriptParser, OpenAiTranscriptParser>(c => c.Timeout = TimeSpan.FromSeconds(30));
// ===== BLOCK: AI_CLIENT_CONFIG END =====


// ===== BLOCK: SWAGGER_CONFIG START =====
// Swagger in Dev
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Make schema IDs unique and handle nested types
    c.CustomSchemaIds(t => t.FullName!.Replace("+", "."));
});
// ===== BLOCK: SWAGGER_CONFIG END =====

builder.Services.AddCeobotSecurity(builder.Configuration);

builder.Services.AddCeobotAccounts(builder.Configuration);

builder.Services.AddSingleton<IEmailSender>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    var smtpHost = cfg["Smtp:Host"];
    if (!string.IsNullOrWhiteSpace(smtpHost))
    {
        return new SmtpEmailSender(cfg, loggerFactory.CreateLogger<SmtpEmailSender>());
    }

    return new DevSinkEmailSender(env, loggerFactory.CreateLogger<DevSinkEmailSender>());
});

// ===== BLOCK: APP_BUILD_AND_MIDDLEWARE START =====
var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseCeobotSecurity(builder.Configuration, app.Environment);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// static files
app.UseDefaultFiles();
app.UseStaticFiles();
// ===== BLOCK: APP_BUILD_AND_MIDDLEWARE END =====

// ===== BLOCK: DB_SEED START =====
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    // Create DB if missing, but DO NOT insert default staff
    await db.Database.EnsureCreatedAsync();

    if (db.Database.IsSqlite())
    {
        await EnsureUserNotificationColumnsAsync(db);
    }
}
// ===== BLOCK: DB_SEED END =====

app.MapCeobotAccounts();

// ===== BLOCK: DIAG_ENDPOINTS START =====
// ===== Diag =====
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/diag/openai", (IConfiguration cfg) =>
{
    var hasKey = !string.IsNullOrWhiteSpace(cfg["OpenAI:ApiKey"]);
    var model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";
    var transcribeModel = cfg["OpenAI:TranscribeModel"] ?? "whisper-1";
    return Results.Ok(new { hasKey, model, transcribeModel });
});

app.MapGet("/api/diag/routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var routes = sources.SelectMany(s => s.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => $"{e.RoutePattern.RawText} [{string.Join(",", e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods ?? new[] { "ANY" })}]");
    return Results.Ok(routes);
});
// ===== BLOCK: DIAG_ENDPOINTS END =====


// ===== BLOCK: CLASSIFY_API START =====
// ===== Classify (AI-only) =====
app.MapPost("/api/classify", async (TicketCreateDto dto, IAIClassifier ai) =>
{
    try
    {
        var (cat, pri, text) = await ai.ClassifyAsync(dto.Description ?? "");
        return Results.Ok(new
        {
            rules = (object?)null,
            ai = new { category = cat.ToString(), priority = pri.ToString(), textPreview = text },
            merged = new { category = cat.ToString(), priority = pri.ToString() },
            aiUsed = true,
            aiStatus = "ok",
            aiErrorPreview = (string?)null
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            rules = (object?)null,
            ai = (object?)null,
            merged = new { category = IssueCategory.Other.ToString(), priority = Priority.Normal.ToString() },
            aiUsed = false,
            aiStatus = "exception",
            aiErrorPreview = ex.Message
        });
    }
});
// ===== BLOCK: CLASSIFY_API END =====

// ===== BLOCK: TICKETS_CREATE_API START =====
// ===== Ticket create (AI) =====
app.MapPost("/api/tickets", async (TicketCreateDto dto, AppDb db, IAIClassifier ai) =>
{
    IssueCategory cat = IssueCategory.Other;
    Priority pri = Priority.Normal;
    try { (cat, pri, _) = await ai.ClassifyAsync(dto.Description ?? ""); } catch { }

    var t = new Ticket
    {
        TenantName = dto.TenantName?.Trim(),
        Unit = dto.Unit?.Trim(),
        Contact = dto.Contact?.Trim(),
        Description = dto.Description?.Trim(),
        Category = cat,
        Priority = pri,
        Status = TicketStatus.Open,
        CreatedUtc = DateTime.UtcNow,
        AssignedStaffId = null
    };
    db.Tickets.Add(t);
    await db.SaveChangesAsync();

    return Results.Ok(new { t.Id, category = t.Category.ToString(), priority = t.Priority.ToString(), status = t.Status.ToString(), assignedTo = "(unassigned)" });
});
// ===== BLOCK: TICKETS_CREATE_API END =====

// ===== BLOCK: ASR_HINT (STEP 1) =====
const string ASR_HINT_QC = @"
Contexte: appels de maintenance résidentielle (FR/EN, accents du Québec).
Termes fréquents: frigidaire, poêle, four, prise, disjoncteur/breaker, panneau électrique, chauffe-eau,
robinet, toilette, évier, lavabo, douche, drain, fuite, dégât d’eau, laveuse, sécheuse,
thermostat, chauffage, air climatisé, clim, HVAC, serrure, poignée, porte d’entrée,
wifi, internet, modem, routeur, insectes, coquerelles, punaises, souris, bruit, plancher, mur.
Noms & unités: “Je m’appelle”, “mon nom c’est”, “j’habite au”, “appartement”, “unité”, “#”.
";

// ===== VOICE: helper uncertainty (new, used by /api/voice*) =====
static (double score, List<string> reasons) HeuristicUncertainty(VoiceParsed p, string transcript)
{
    var reasons = new List<string>();
    double s = 0;

    bool NameBad(string? x)
    {
        if (string.IsNullOrWhiteSpace(x)) return true;
        var v = x.ToLowerInvariant();
        if (v.Length > 40) return true;
        if (Regex.IsMatch(v, @"\d")) return true;
        if (Regex.IsMatch(v, @"\b(appartement|unité|apt|unit|apartment|rue|avenue|boulevard|ch.[ae]min|chemin)\b")) return true;
        if (Regex.IsMatch(v, @"\b(demain|aujourd'hui|matin|soir|journ[eé]e|maison)\b")) return true;
        return false;
    }

    if (NameBad(p.Name)) { s += 0.20; reasons.Add("nameMissingOrSuspect"); }
    if (string.IsNullOrWhiteSpace(p.Unit)) { s += 0.20; reasons.Add("unitMissing"); }
    if (string.IsNullOrWhiteSpace(p.Address) || p.Address!.Trim().Length < 6) { s += 0.30; reasons.Add("addressMissingOrShort"); }
    if (Regex.IsMatch(transcript, @"\b(sorry|désol[eé]|en fait|actually|correction)\b", RegexOptions.IgnoreCase)) { s += 0.20; reasons.Add("selfCorrectionDetected"); }
    if ((transcript ?? "").Length < 20) { s += 0.10; reasons.Add("veryShortTranscript"); }

    if (s < 0) s = 0; if (s > 1) s = 1;
    return (s, reasons);
}

// ===== BLOCK: VOICE_TRANSCRIBE_PREVIEW_API START =====
// Preview-only: accepts multipart/form-data with "audio"; returns transcript + parsed fields (AI-first)
// NOTE: description in the response is ALWAYS the full transcript (per your request).
app.MapPost("/api/voice/transcribe", async (HttpRequest req, IAudioTranscriber stt, ITranscriptParser parser) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "audio file missing" });

    // Optional language hint: ?lang=fr or ?lang=en (anything else = auto)
    var qLang = (req.Query["lang"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
    string? langHint = qLang is "fr" or "en" ? qLang : null;

    // Read once so we can retry in the other language if needed
    byte[] audioBytes;
    await using (var s = file.OpenReadStream())
    {
        audioBytes = new byte[file.Length];
        int read, offset = 0;
        while ((read = await s.ReadAsync(audioBytes.AsMemory(offset))) > 0) offset += read;
    }

    async Task<string> PassAsync(string? lang)
    {
        using var ms = new MemoryStream(audioBytes, writable: false);
        return await stt.TranscribeAsync(
            ms,
            file.FileName,
            file.ContentType ?? "application/octet-stream",
            lang,           // language hint (null = auto)
            ASR_HINT_QC     // bias vocab for QC maintenance
        );
    }

    // First pass (hint if provided, else auto)
    var transcript = await PassAsync(langHint);

    // Fallback in the other likely language if too weak
    static bool Weak(string s) => string.IsNullOrWhiteSpace(s) || s.Trim().Length < 6;
    bool fallbackUsed = false;
    if (Weak(transcript))
    {
        var other = langHint == "fr" ? "en" : langHint == "en" ? "fr" : "fr";
        transcript = await PassAsync(other);
        fallbackUsed = true;
        if (!Weak(transcript)) langHint = other;
    }

    if (Weak(transcript))
        return Results.BadRequest(new { error = "transcription too weak", langTried = langHint ?? "auto", fallbackUsed });

    // AI-first parse (regex salvage inside)
    var parsed = await parser.ParseAsync(transcript);
    // Make description THE FULL MESSAGE (not the LLM's description)
    parsed.Description = transcript.Trim();

    // light uncertainty score
    var (score, reasons) = HeuristicUncertainty(parsed, transcript);

    return Results.Ok(new
    {
        text = transcript,
        langUsed = parsed.Lang ?? (langHint ?? "auto"),
        fallbackUsed,
        parsed,
        uncertainty = new { score, reasons }
    });
});
// ===== BLOCK: VOICE_TRANSCRIBE_PREVIEW_API END =====

// ===== VOICE_TEXT_ONLY_DEBUG_API (new) =====
// Quick tester: POST { "text": "Bonjour..."}  -> same shape as /api/voice/transcribe but without audio
app.MapPost("/api/voice/parse", async (JsonElement body, ITranscriptParser parser) =>
{
    var text = body.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
    text = text.Trim();
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "text required" });

    var parsed = await parser.ParseAsync(text);
    parsed.Description = text; // full transcript as description
    var (score, reasons) = HeuristicUncertainty(parsed, text);

    return Results.Ok(new
    {
        text,
        langUsed = parsed.Lang ?? "auto",
        fallbackUsed = false,
        parsed,
        uncertainty = new { score, reasons }
    });
});

// ===== BLOCK: VOICE_SUBMIT_API START (PATCHED STEP 2) =====
// Accepts audio/* (webm/wav/m4a/mp3) from the voice page, transcribes, parses, classifies, and creates a ticket.
// NOTE: description saved to DB is ALWAYS the full transcript.
app.MapPost("/api/voice", async (HttpRequest req, AppDb db, IAudioTranscriber stt, ITranscriptParser parser, IAIClassifier ai, IEmailSender emailSender, ILogger<Program> logger) =>
{
    if (!req.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "audio file missing" });

    string? notifyEmailValue = form["notifyEmail"].FirstOrDefault()?.Trim();
    if (string.IsNullOrWhiteSpace(notifyEmailValue)) notifyEmailValue = null;
    if (notifyEmailValue is not null && !EmailValidation.IsValid(notifyEmailValue))
        return Results.BadRequest(new { error = "invalid notifyEmail" });

    var notifyMeRaw = form["notifyMe"].FirstOrDefault();
    bool notifyMe = IsTruthy(notifyMeRaw);

    UserAccount? account = null;
    if (notifyMe)
    {
        var principal = req.HttpContext?.User;
        var idVal = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal?.Identity?.IsAuthenticated == true && int.TryParse(idVal, out var accId))
        {
            account = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accId);
        }

        if (account is null)
            return Results.BadRequest(new { error = "notifyMe requires an authenticated account" });

        if (!account.NotifyVoiceEnabled || string.IsNullOrWhiteSpace(account.NotifyEmail))
            return Results.BadRequest(new { error = "account notifications not enabled" });

        if (!EmailValidation.IsValid(account.NotifyEmail))
        {
            logger.LogWarning("Stored notify email invalid for account {AccountId}", account.Id);
            return Results.BadRequest(new { error = "account notify email invalid" });
        }
    }

    var notifyTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (notifyEmailValue is not null) notifyTargets.Add(notifyEmailValue);
    if (notifyMe && account?.NotifyEmail is { } acctEmail)
        notifyTargets.Add(acctEmail);

    // Optional language hint: ?lang=fr or ?lang=en (anything else = auto)
    var qLang = (req.Query["lang"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
    string? langHint = qLang is "fr" or "en" ? qLang : null;

    // Read once into memory so we can do a fallback pass if needed
    byte[] audioBytes;
    await using (var s = file.OpenReadStream())
    {
        audioBytes = new byte[file.Length];
        int read = 0, offset = 0;
        while ((read = await s.ReadAsync(audioBytes.AsMemory(offset))) > 0) offset += read;
    }

    async Task<string> PassAsync(string? lang)
    {
        using var ms = new MemoryStream(audioBytes, writable: false);
        return await stt.TranscribeAsync(
            ms,
            file.FileName,
            file.ContentType ?? "application/octet-stream",
            lang,                // language hint (null = auto)
            ASR_HINT_QC          // biasing prompt for QC maintenance vocab
        );
    }

    // First pass (hint if provided, else auto)
    var transcript = await PassAsync(langHint);

    // If the first pass is too short/empty, try the other likely language as fallback
    bool fallbackUsed = false;
    static bool Weak(string s) => string.IsNullOrWhiteSpace(s) || s.Trim().Length < 6;
    if (Weak(transcript))
    {
        var other = langHint == "fr" ? "en" : langHint == "en" ? "fr" : "fr";
        transcript = await PassAsync(other);
        fallbackUsed = true;
        if (!Weak(transcript)) langHint = other; // record the lang that worked
    }

    // Final guard: still weak? Return but mark error (don’t create garbage tickets)
    if (Weak(transcript))
        return Results.BadRequest(new { error = "transcription too weak", langTried = langHint ?? "auto", fallbackUsed });

    // Parse structured fields with LLM (regex salvage inside)
    var p = await parser.ParseAsync(transcript);
    p.Description = transcript.Trim(); // FULL MESSAGE as description

    // Classify using the full transcript
    IssueCategory cat = IssueCategory.Other;
    Priority pri = Priority.Normal;
    try { (cat, pri, _) = await ai.ClassifyAsync(transcript); } catch { }

    var t = new Ticket
    {
        TenantName = string.IsNullOrWhiteSpace(p.Name) ? "(voice)" : p.Name!.Trim(),
        Unit = p.Unit?.Trim() ?? p.Address?.Trim(),
        Contact = p.Contact?.Trim(),
        Description = transcript.Trim(),
        Category = cat,
        Priority = pri,
        Status = TicketStatus.Open,
        CreatedUtc = DateTime.UtcNow,
        AssignedStaffId = null
    };
    db.Tickets.Add(t);
    await db.SaveChangesAsync();

    var (score, reasons) = HeuristicUncertainty(p, transcript);

    var notifyResults = new List<(string email, EmailSendResult result)>();
    if (notifyTargets.Count > 0)
    {
        var subject = $"Ticket #{t.Id}: Copy of your voice submission";
        var body = BuildVoiceNotificationBody(t, p, transcript, score, reasons);

        foreach (var email in notifyTargets)
        {
            EmailSendResult result;
            try
            {
                result = await emailSender.SendAsync(new EmailMessage(email, subject, body));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NotificationFailed ticket {TicketId} -> {Target}", t.Id, RedactEmailForLog(email));
                result = new EmailSendResult(false, null, ex.Message);
            }

            notifyResults.Add((email, result));

            if (result.Sent)
            {
                logger.LogInformation("NotificationSent ticket {TicketId} -> {Target}", t.Id, RedactEmailForLog(email));
            }
            else
            {
                logger.LogWarning("NotificationFailed ticket {TicketId} -> {Target} ({Error})", t.Id, RedactEmailForLog(email), result.Error ?? "unknown error");
            }
        }
    }

    var notificationPayload = notifyResults
        .Select(r => new { email = r.email, sent = r.result.Sent, devPath = r.result.DevFilePath, error = r.result.Error })
        .ToArray();

    var devEmailPaths = notifyResults
        .Select(r => r.result.DevFilePath)
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .ToArray();

    return Results.Ok(new
    {
        id = t.Id,
        transcript,
        parsed = new { p.Name, p.Unit, p.Address, p.Contact, p.Description, langUsed = p.Lang ?? langHint ?? "auto" },
        category = t.Category.ToString(),
        priority = t.Priority.ToString(),
        uncertainty = new { score, reasons },
        needsReview = score >= 0.50,
        reviewNotes = reasons,
        notifications = notificationPayload,
        notificationAttempted = notifyTargets.Count,
        devEmailPaths = devEmailPaths.Length > 0 ? devEmailPaths : null
    });
});
// ===== BLOCK: VOICE_SUBMIT_API END =====

app.MapVoiceTelephony();

// ===== BLOCK: STAFF_APIS START =====
// ===== STAFF =====

// Simple login check (for debugging & the staff UI to verify key)
app.MapPost("/api/staff/login", async (LoginDto body, AppDb db, HttpRequest req) =>
{
    var ok = await StaffAuthorized(db, body.Id, body.Key, req);
    if (!ok) return Results.Unauthorized();
    var staff = await db.Staff.Where(s => s.Id == body.Id).Select(s => new
    {
        s.Id,
        s.Name,
        s.IsActive,
        s.IsAdmin,
        handles = s.Handles.ToString()
    }).FirstAsync();
    return Results.Ok(staff);
});

// Unassigned for my roles
app.MapGet("/api/staff/{id:int}/unassigned", async (int id, string? key, HttpRequest req, AppDb db) =>
{
    if (!await StaffAuthorized(db, id, key, req)) return Results.Unauthorized();
    var me = await db.Staff.FindAsync(id); if (me == null || !me.IsActive) return Results.Unauthorized();

    var tickets = await db.Tickets
        .Where(t => t.Status == TicketStatus.Open && t.AssignedStaffId == null && (me.Handles & t.Category) != 0)
        .OrderBy(t => t.CreatedUtc)
        .Select(t => new { t.Id, category = t.Category.ToString(), priority = t.Priority.ToString(), t.TenantName, t.Unit, t.Contact, t.Description, t.CreatedUtc })
        .ToListAsync();

    return Results.Ok(tickets);
});

// Mine (anything assigned to me that isn't completed)
app.MapGet("/api/staff/{id:int}/mine", async (int id, string? key, HttpRequest req, AppDb db) =>
{
    if (!await StaffAuthorized(db, id, key, req)) return Results.Unauthorized();
    var tickets = await db.Tickets
        .Where(t => t.AssignedStaffId == id && t.Status != TicketStatus.Completed)
        .OrderBy(t => t.CreatedUtc)
        .Select(t => new { t.Id, category = t.Category.ToString(), priority = t.Priority.ToString(), status = t.Status.ToString(), t.TenantName, t.Unit, t.Contact, t.Description, t.CreatedUtc })
        .ToListAsync();
    return Results.Ok(tickets);
});

app.MapPost("/api/staff/{id:int}/claim/{ticketId:int}", async (int id, int ticketId, string? key, HttpRequest req, AppDb db) =>
{
    if (!await StaffAuthorized(db, id, key, req)) return Results.Unauthorized();
    var me = await db.Staff.FindAsync(id);
    var t = await db.Tickets.FindAsync(ticketId);
    if (me == null || t == null) return Results.NotFound();
    if (t.AssignedStaffId != null && t.AssignedStaffId != id) return Results.BadRequest(new { error = "Already claimed by someone else." });
    if ((me.Handles & t.Category) == 0) return Results.BadRequest(new { error = "You don't handle this category." });

    t.AssignedStaffId = id;
    t.Status = TicketStatus.Ongoing;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/staff/{id:int}/complete/{ticketId:int}", async (int id, int ticketId, string? key, HttpRequest req, AppDb db) =>
{
    if (!await StaffAuthorized(db, id, key, req)) return Results.Unauthorized();
    var t = await db.Tickets.FindAsync(ticketId);
    if (t == null || t.AssignedStaffId != id) return Results.BadRequest(new { error = "Not your ticket." });

    t.Status = TicketStatus.Completed;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});
// ===== BLOCK: STAFF_APIS END =====

// ===== BLOCK: ADMIN_APIS START =====
// ===== ADMIN =====
// Requires: using System.Security.Claims;
app.MapGet("/api/admin/me", (ClaimsPrincipal user) =>
{
    var isAuth = user?.Identity?.IsAuthenticated == true;

    if (isAuth)
    {
        return Results.Ok(new
        {
            id = user.FindFirstValue(ClaimTypes.NameIdentifier),
            email = user.FindFirstValue(ClaimTypes.Email),
            role = user.FindFirstValue(ClaimTypes.Role),
            via = "cookie"   // came through JWT cookie
        });
    }

    // If you reached here without a cookie, Security.cs allowed the X-Admin-Key header.
    return Results.Ok(new { ok = true, via = "header-key" });
});

// ===== BLOCK: ADMIN_DASHBOARD_API START =====
app.MapGet("/api/admin/dashboard/summary", async (HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var tickets = await db.Tickets.AsNoTracking().ToListAsync();

    int Total() => tickets.Count;
    int CountStatus(TicketStatus s) => tickets.Count(t => t.Status == s);
    int CountPriority(Priority p) => tickets.Count(t => t.Priority == p);

    // By category (string -> int)
    var byCategory = Enum.GetValues<IssueCategory>()
        .Where(c => c != IssueCategory.None)
        .ToDictionary(c => c.ToString(), c => tickets.Count(t => t.Category == c));

    // By priority
    var byPriority = new Dictionary<string, int>
    {
        ["Normal"] = CountPriority(Priority.Normal),
        ["Urgent"] = CountPriority(Priority.Urgent),
        ["Emergency"] = CountPriority(Priority.Emergency),
    };

    // By status
    var byStatus = new Dictionary<string, int>
    {
        ["Open"] = CountStatus(TicketStatus.Open),
        ["Ongoing"] = CountStatus(TicketStatus.Ongoing),
        ["Completed"] = CountStatus(TicketStatus.Completed),
    };

    // Last 14 days – created per day
    var start = DateTime.UtcNow.Date.AddDays(-13);
    var days = Enumerable.Range(0, 14).Select(i => start.AddDays(i)).ToList();
    var createdByDay = days.Select(d => new
    {
        date = d.ToString("yyyy-MM-dd"),
        created = tickets.Count(t => t.CreatedUtc.Date == d)
    });

    return Results.Ok(new
    {
        total = Total(),
        byStatus,
        byCategory,
        byPriority,
        recent = createdByDay
    });
});

app.MapGet("/api/admin/dashboard/staff", async (HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var staff = await db.Staff.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
    var tickets = await db.Tickets.AsNoTracking().ToListAsync();

    var list = staff.Select(s =>
    {
        var mine = tickets.Where(t => t.AssignedStaffId == s.Id);
        var open = mine.Count(t => t.Status == TicketStatus.Open);
        var ongoing = mine.Count(t => t.Status == TicketStatus.Ongoing);
        var completed = mine.Count(t => t.Status == TicketStatus.Completed);
        return new
        {
            id = s.Id,
            name = s.Name,
            isActive = s.IsActive,
            totalAssigned = mine.Count(),
            open,
            ongoing,
            completed
        };
    });

    return Results.Ok(list);
});
// ===== BLOCK: ADMIN_DASHBOARD_API END =====


// ===== BLOCK: ADMIN_TICKETS_API START =====

// List tickets with optional filters: ?status=Open|Ongoing|Completed & ?category=Electrical|Plumbing|...
// Includes assignedStaffId so the UI can preselect the assignee.
async Task<IResult> AdminListTickets(HttpRequest req, IConfiguration cfg, AppDb db)
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var statusQ = req.Query["status"].FirstOrDefault();
    var categoryQ = req.Query["category"].FirstOrDefault();

    var q = db.Tickets.AsQueryable();

    if (!string.IsNullOrWhiteSpace(statusQ) && Enum.TryParse<TicketStatus>(statusQ, true, out var wantedStatus))
        q = q.Where(t => t.Status == wantedStatus);

    if (!string.IsNullOrWhiteSpace(categoryQ) && Enum.TryParse<IssueCategory>(categoryQ, true, out var wantedCat))
        q = q.Where(t => t.Category == wantedCat);

    var list = await q
        .OrderByDescending(t => t.CreatedUtc)
        .Select(t => new
        {
            t.Id,
            category = t.Category.ToString(),
            priority = t.Priority.ToString(),
            status = t.Status.ToString(),
            t.TenantName,
            t.Unit,
            t.Contact,
            t.Description,
            t.CreatedUtc,
            assignedTo = t.AssignedStaff != null ? t.AssignedStaff.Name : "(unassigned)",
            assignedStaffId = t.AssignedStaffId
        })
        .ToListAsync();

    return Results.Ok(list);
}

// Keep both routes for compatibility
app.MapGet("/api/tickets", AdminListTickets);
app.MapGet("/api/admin/tickets", AdminListTickets);

// Assign/unassign a ticket to a staff member.
// If staffId == 0, unassign. Otherwise set AssignedStaffId and bump status to Ongoing if it was Open.
app.MapPost("/api/admin/tickets/{id:int}/assign/{staffId:int}", async (int id, int staffId, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var t = await db.Tickets.FindAsync(id);
    if (t == null) return Results.NotFound();

    if (staffId == 0)
    {
        t.AssignedStaffId = null;
    }
    else
    {
        var s = await db.Staff.FindAsync(staffId);
        if (s == null || !s.IsActive) return Results.BadRequest(new { error = "Invalid staff id." });
        t.AssignedStaffId = staffId;
        if (t.Status == TicketStatus.Open) t.Status = TicketStatus.Ongoing;
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Update status for a ticket (Open/Ongoing/Completed)
app.MapPost("/api/admin/tickets/{id:int}/status", async (int id, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var t = await db.Tickets.FindAsync(id);
    if (t == null) return Results.NotFound();

    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var st = JsonDocument.Parse(body).RootElement.TryGetProperty("status", out var p) ? p.GetString() : null;
    if (!Enum.TryParse<TicketStatus>(st, true, out var newS)) return Results.BadRequest(new { error = "Invalid status" });

    t.Status = newS;
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// Permanently delete a ticket
app.MapDelete("/api/admin/tickets/{id:int}", async (int id, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    if (!AdminAuthorized(req, cfg)) return Results.Unauthorized();

    var t = await db.Tickets.FindAsync(id);
    if (t == null) return Results.NotFound();

    db.Tickets.Remove(t);
    await db.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ===== BLOCK: ADMIN_TICKETS_API END =====


// ===== (REMOVED DUPLICATE ROUTES) =====
// Removed duplicate mapping block:
//   - /api/admin/tickets/{id:int}/assign/{staffId:int}
// Removed duplicate mapping block:
//   - /api/admin/tickets/{id:int}/status
// The first/canonical ones above remain and are used by the UI.

// *** FIX: return handles as array ***
app.MapGet("/api/admin/staff", async (HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    

    var allCats = Enum.GetValues<IssueCategory>().Where(c => c != IssueCategory.None).ToArray();

    var raw = await db.Staff.OrderBy(s => s.Id).Select(s => new
    {
        s.Id,
        s.Name,
        s.Email,
        s.Phone,
        s.Role,
        s.IsActive,
        s.IsAdmin,
        s.PreferredChannel,
        s.AccessKey,
        HandlesMask = s.Handles
    }).ToListAsync();

    var list = raw.Select(s => new
    {
        s.Id,
        s.Name,
        s.Email,
        s.Phone,
        s.Role,
        s.IsActive,
        s.IsAdmin,
        preferredChannel = s.PreferredChannel,
        s.AccessKey,
        handles = allCats.Where(c => (s.HandlesMask & c) != 0).Select(c => c.ToString()).ToArray()
    });

    return Results.Ok(list);
}).RequireAuthorization("Admin");

// *** FIX: accept categories OR handles from body ***
app.MapPost("/api/admin/staff", async (HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var dto = JsonSerializer.Deserialize<StaffUpsertDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

    var selected = dto.Handles ?? dto.Categories;

    var s = new StaffMember
    {
        Name = dto.Name?.Trim() ?? "New Staff",
        Email = dto.Email?.Trim(),
        Phone = dto.Phone?.Trim(),
        Role = dto.Role?.Trim(),
        PreferredChannel = dto.PreferredChannel?.Trim() ?? "Email",
        Handles = ParseHandles(selected),
        IsActive = dto.IsActive,
        IsAdmin = dto.IsAdmin,
        AccessKey = Guid.NewGuid().ToString("N")
    };
    db.Staff.Add(s); await db.SaveChangesAsync();
    return Results.Ok(new { s.Id, s.Name, s.AccessKey });
}).RequireAuthorization("Admin");

app.MapPut("/api/admin/staff/{id:int}", async (int id, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    
    var s = await db.Staff.FindAsync(id); if (s == null) return Results.NotFound();
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var dto = JsonSerializer.Deserialize<StaffUpsertDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

    var selected = dto.Handles ?? dto.Categories;

    s.Name = dto.Name?.Trim() ?? s.Name; s.Email = dto.Email?.Trim(); s.Phone = dto.Phone?.Trim();
    s.Role = dto.Role?.Trim(); s.PreferredChannel = dto.PreferredChannel?.Trim() ?? s.PreferredChannel;
    s.IsActive = dto.IsActive; s.IsAdmin = dto.IsAdmin; s.Handles = ParseHandles(selected);
    await db.SaveChangesAsync(); return Results.Ok(new { ok = true });
}).RequireAuthorization("Admin");

app.MapPost("/api/admin/staff/{id:int}/rotate", async (int id, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    
    var s = await db.Staff.FindAsync(id); if (s == null) return Results.NotFound();
    s.AccessKey = Guid.NewGuid().ToString("N"); await db.SaveChangesAsync();
    return Results.Ok(new { s.Id, s.AccessKey });
}).RequireAuthorization("Admin");

// Permanently delete a staff member (unassigns their tickets first)
app.MapDelete("/api/admin/staff/{id:int}", async (int id, HttpRequest req, IConfiguration cfg, AppDb db) =>
{
    

    var s = await db.Staff.FindAsync(id);
    if (s == null) return Results.NotFound();

    // Unassign any tickets that reference this staff member, and reopen if needed
    var affected = await db.Tickets.Where(t => t.AssignedStaffId == id).ToListAsync();
    foreach (var t in affected)
    {
        t.AssignedStaffId = null;
        if (t.Status != TicketStatus.Completed && t.Status != TicketStatus.Open)
            t.Status = TicketStatus.Open;
    }
    await db.SaveChangesAsync(); // persist the unassignments first

    db.Staff.Remove(s);
    await db.SaveChangesAsync();

    return Results.Ok(new { ok = true, unassigned = affected.Count });
}).RequireAuthorization("Admin");


// ===== BLOCK: ADMIN_APIS END =====

// ===== BLOCK: APP_RUN START =====
app.Run();
// ===== BLOCK: APP_RUN END =====

// ===== BLOCK: HELPERS_AND_MODELS START =====
// ===== Helpers / Models / AI =====

static async Task EnsureUserNotificationColumnsAsync(AppDb db)
{
    var statements = new[]
    {
        "ALTER TABLE UserAccounts ADD COLUMN NotifyEmail TEXT NULL",
        "ALTER TABLE UserAccounts ADD COLUMN NotifyVoiceEnabled INTEGER NOT NULL DEFAULT 0"
    };

    foreach (var sql in statements)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (SqliteException ex) when (IsSqliteDuplicateColumn(ex))
        {
            // Column already exists; no action needed.
        }
    }
}

static bool IsSqliteDuplicateColumn(SqliteException ex)
    => ex.SqliteErrorCode == 1
       && (ex.Message?.IndexOf("duplicate column name", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;

static bool IsTruthy(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return false;

    return value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase);
}

static string RedactEmailForLog(string email)
{
    if (string.IsNullOrWhiteSpace(email)) return "(blank)";

    var at = email.IndexOf('@');
    if (at <= 0 || at >= email.Length - 1) return "***";

    var local = email.Substring(0, at);
    var domain = email.Substring(at + 1);

    var dot = domain.IndexOf('.');
    var domainName = dot >= 0 ? domain[..dot] : domain;
    var suffix = dot >= 0 ? domain[dot..] : string.Empty;

    string maskedDomain = domainName.Length switch
    {
        0 => "***",
        1 => $"{domainName[0]}***",
        2 => $"{domainName[0]}*{domainName[1]}",
        _ => $"{domainName[0]}{new string('*', Math.Max(domainName.Length - 2, 1))}{domainName[^1]}"
    };

    return $"{local}@{maskedDomain}{suffix}";
}

static string BuildVoiceNotificationBody(Ticket ticket, VoiceParsed parsed, string transcript, double uncertaintyScore, IReadOnlyCollection<string> reasons)
{
    static string Format(string? value) => string.IsNullOrWhiteSpace(value) ? "(not provided)" : value.Trim();

    var sb = new StringBuilder();
    sb.AppendLine("We received your voice maintenance request. Here's a copy for your records.");
    sb.AppendLine();
    sb.AppendLine($"Ticket #: {ticket.Id}");
    sb.AppendLine($"Submitted (UTC): {ticket.CreatedUtc:yyyy-MM-dd HH:mm}");
    sb.AppendLine($"Category: {ticket.Category}");
    sb.AppendLine($"Priority: {ticket.Priority}");
    sb.AppendLine();
    sb.AppendLine($"Name: {Format(parsed.Name ?? ticket.TenantName)}");
    sb.AppendLine($"Unit/Address: {Format(parsed.Unit ?? parsed.Address ?? ticket.Unit)}");
    sb.AppendLine($"Contact: {Format(parsed.Contact ?? ticket.Contact)}");
    sb.AppendLine();
    sb.AppendLine("Full transcript:");
    sb.AppendLine(transcript.Trim());
    sb.AppendLine();
    sb.AppendLine($"System uncertainty score: {Math.Clamp(uncertaintyScore, 0, 1):0.00}");
    if (reasons.Count > 0)
    {
        sb.AppendLine("Flags: " + string.Join(", ", reasons));
        sb.AppendLine();
    }

    sb.AppendLine("If anything looks off, reply to this email and we'll take another look.");
    sb.AppendLine();
    sb.AppendLine("— CEObot");

    return sb.ToString();
}

static bool AdminAuthorized(HttpRequest req, IConfiguration cfg)
{
    var supplied = req.Headers["X-Admin-Key"].FirstOrDefault()?.Trim();
    var configured = cfg["Admin:Key"]?.Trim();
    return !string.IsNullOrWhiteSpace(supplied)
        && !string.IsNullOrWhiteSpace(configured)
        && string.Equals(supplied, configured, StringComparison.Ordinal);
}


static async Task<bool> StaffAuthorized(AppDb db, int id, string? key, HttpRequest req)
{
    // Header-only: ignore query param entirely
    var supplied = req.Headers["X-Staff-Key"].FirstOrDefault()?.Trim();
    if (string.IsNullOrEmpty(supplied)) return false;

    var s = await db.Staff
        .Where(x => x.Id == id)
        .Select(x => new { x.AccessKey, x.IsActive })
        .FirstOrDefaultAsync();

    return s != null && s.IsActive &&
           string.Equals(s.AccessKey?.Trim(), supplied, StringComparison.OrdinalIgnoreCase);
}


static IssueCategory ParseHandles(IEnumerable<string>? handles)
{
    if (handles == null) return IssueCategory.Other;
    IssueCategory m = 0; foreach (var h in handles)
        if (Enum.TryParse<IssueCategory>(h?.Trim() ?? "", true, out var c)) m |= c;
    return m == 0 ? IssueCategory.Other : m;
}

public class AppDb : DbContext
{
    public AppDb(DbContextOptions<AppDb> options) : base(options) { }

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Unique email (case-insensitive via NormalizedEmail stored UPPER)
        modelBuilder.Entity<UserAccount>()
            .HasIndex(u => u.NormalizedEmail)
            .IsUnique();
    }

}

[Flags] public enum IssueCategory { None = 0, Electrical = 1, Plumbing = 2, HVAC = 4, LocksDoors = 8, Appliances = 16, Gas = 32, Internet = 64, Pest = 128, Structural = 256, Noise = 512, Other = 1024, Emergency = 2048 }
public enum TicketStatus { Open, Assigned, Ongoing, Completed }
public enum Priority { Normal, Urgent, Emergency }

public class Ticket
{
    public int Id { get; set; }
    public string? TenantName { get; set; }
    public string? Unit { get; set; }
    public string? Contact { get; set; }
    public string? Description { get; set; }
    public IssueCategory Category { get; set; }
    public Priority Priority { get; set; } = Priority.Normal;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public DateTime CreatedUtc { get; set; }
    public int? AssignedStaffId { get; set; }
    public StaffMember? AssignedStaff { get; set; }
}

public class StaffMember
{
    public int Id { get; set; }
    public string Name { get; set; } = "Staff";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Role { get; set; }
    public string PreferredChannel { get; set; } = "Email";
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; } = false;
    public IssueCategory Handles { get; set; } = IssueCategory.Other;
    public string AccessKey { get; set; } = Guid.NewGuid().ToString("N");
}

public class TicketCreateDto { public string? TenantName { get; set; } public string? Unit { get; set; } public string? Contact { get; set; } public string? Description { get; set; } }

// ⬇️ DTOs placed here
public class StaffUpsertDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Role { get; set; }
    public bool IsActive { get; set; } = true; public bool IsAdmin { get; set; } = false;
    public string? PreferredChannel { get; set; } = "Email";

    // *** FIX: accept both for compatibility with UI ***
    public List<string>? Handles { get; set; }
    public List<string>? Categories { get; set; }
}

public class LoginDto { public int Id { get; set; } public string? Key { get; set; } }
// ⬆️ DTOs placed here

// ===== BLOCK: HELPERS_AND_MODELS END =====

// ===== BLOCK: AI_CLASSIFIER START =====

public interface IAIClassifier
{
    Task<(IssueCategory category, Priority priority, string rawText)> ClassifyAsync(string description);
}

public class OpenAiClassifier : IAIClassifier
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _model;

    public OpenAiClassifier(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _key = cfg["OpenAI:ApiKey"] ?? "";
        _model = cfg["OpenAI:Model"] ?? "gpt-4o-mini";
    }

    public async Task<(IssueCategory, Priority, string)> ClassifyAsync(string description)
    {
        if (string.IsNullOrWhiteSpace(_key))
            throw new InvalidOperationException("OpenAI:ApiKey missing.");

        // ===== BLOCK: AI_CLASSIFIER_PROMPT START =====
        var payload = new
        {
            model = _model,
            temperature = 0, // deterministic & conservative
            messages = new object[]
            {
                new {
                    role = "system",
                    content =
@"You are a property-maintenance triage assistant.

The user message can be **English or French**. You must output **ONLY a JSON object** with this exact shape:
{""category"":""<one>"",""priority"":""<one>""}

Categories (use these English labels only): Electrical, Plumbing, HVAC, LocksDoors, Appliances, Gas, Internet, Pest, Structural, Noise, Other, Emergency.
Priorities (use these English labels only): Emergency, Urgent, Normal.

Decide from concrete details, not rhetoric. If uncertain, choose **Normal**.

Priority rubric:
- Emergency (life-safety / active damage):
  EN: gas smell/hissing, CO alarm, smoke/sparks/fire, flooding/major burst, structural collapse,
      stuck door trapping occupants, power outage to the whole unit, no heat in dangerously cold conditions.
  FR: odeur de gaz / sifflement, alarme CO, fumée/étincelles/incendie, inondation/rupture majeure,
      effondrement, bloqué à l’intérieur, panne de courant de tout le logement,
      pas de chauffage par grand froid dangereux.
- Urgent (time-sensitive but not life-safety):
  EN: no hot water, fridge not cooling, single-bath toilet unusable, exterior door won’t lock,
      continuous leak that can be contained, HVAC out but indoor temp still safe,
      power out to one room.
  FR: pas d’eau chaude, frigo ne refroidit pas, toilette inutilisable (unique sdb),
      porte qui ne se verrouille pas, fuite continue mais contenue, HVAC en panne
      mais température intérieure sûre, panne partielle.
- Normal (cosmetic/intermittent/has workaround):
  EN: slow drip, flickering light, intermittent noise, loose handle, internet slowness.
  FR: goutte lente, lumière qui clignote, bruit intermittent, poignée lâche, internet lent.

Guidance:
- Ignore rhetoric like ""urgent"", ""ASAP"", ""emergency"" unless details justify it. (FR: ignorer ""urgent"", ""tout de suite"", etc.)
- Respect negations like ""no smell/sparks"", ""small/slow drip"", ""can wait"". (FR: ""pas d’odeur/étincelles"", ""petite fuite"", ""peut attendre"".)
- Map French descriptions to the English **category** labels above.
- If symptoms span multiple categories, pick the **primary trade** you would dispatch first.
- Output ONLY the JSON object — no extra text."
                },

                // Few-shot: rhetoric without hazard -> Normal (EN)
                new { role = "user", content = "URGENT faucet is dripping slowly in the bathroom. No flooding, just a drip into the sink. Can wait until tomorrow." },
                new { role = "assistant", content = "{\"category\":\"Plumbing\",\"priority\":\"Normal\"}" },

                // Few-shot: clear hazard -> Emergency (FR)
                new { role = "user", content = "Il y a une forte odeur de gaz près de la cuisinière et on entend un sifflement." },
                new { role = "assistant", content = "{\"category\":\"Gas\",\"priority\":\"Emergency\"}" },

                // Few-shot: essential service outage -> Urgent (EN)
                new { role = "user", content = "Fridge is at 50°F and food is getting warm. No other issues, no kids or meds to store." },
                new { role = "assistant", content = "{\"category\":\"Appliances\",\"priority\":\"Urgent\"}" },

                // Few-shot: partial power, still safe -> Urgent (EN)
                new { role = "user", content = "Living room outlets lost power after a breaker tripped. Reset works for a bit then trips again. No burning smell." },
                new { role = "assistant", content = "{\"category\":\"Electrical\",\"priority\":\"Urgent\"}" },

                // Few-shot: cosmetic/intermittent -> Normal (FR)
                new { role = "user", content = "Une lumière clignote de temps en temps dans la cuisine, pas d’odeur, rien de chaud, ça peut attendre." },
                new { role = "assistant", content = "{\"category\":\"Electrical\",\"priority\":\"Normal\"}" },

                // Few-shot: security but not life-safety -> Urgent (EN)
                new { role = "user", content = "Front door deadbolt won't lock. Door closes but can't secure it." },
                new { role = "assistant", content = "{\"category\":\"LocksDoors\",\"priority\":\"Urgent\"}" },

                // Actual user message
                new { role = "user", content = description ?? string.Empty }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _key);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI {res.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        // Extract JSON object if any extra text slipped in
        var s = text.Trim();
        var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
        if (i >= 0 && j > i) s = s.Substring(i, j - i + 1);

        string? catStr = null, priStr = null;
        try
        {
            using var inner = JsonDocument.Parse(s);
            catStr = inner.RootElement.GetProperty("category").GetString();
            priStr = inner.RootElement.GetProperty("priority").GetString();
        }
        catch
        {
            // fall back below
        }

        // Map to enums with safe defaults
        IssueCategory cat = IssueCategory.Other;
        Priority pri = Priority.Normal;
        if (!string.IsNullOrWhiteSpace(catStr) && Enum.TryParse<IssueCategory>(catStr, true, out var c)) cat = c;
        if (!string.IsNullOrWhiteSpace(priStr) && Enum.TryParse<Priority>(priStr, true, out var p)) pri = p;

        // ===== BLOCK: AI_CLASSIFIER_PROMPT END =====
        return (cat, pri, text);
    }
}
// ===== BLOCK: AI_CLASSIFIER END =====

// ===== BLOCK: AUDIO_TRANSCRIBER START =====
public interface IAudioTranscriber
{
    // Existing simple version (kept for compatibility)
    Task<string> TranscribeAsync(Stream audio, string fileName, string contentType);

    // New overload with language + prompt (used by /api/voice)
    Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, string? language, string? prompt);
}

public class OpenAIWhisperTranscriber : IAudioTranscriber
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _model;

    public OpenAIWhisperTranscriber(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _key = cfg["OpenAI:ApiKey"] ?? "";
        // Use the new lightweight STT by default; override via appsettings if you prefer "whisper-1"
        _model = cfg["OpenAI:TranscribeModel"] ?? "gpt-4o-mini-transcribe";
    }

    // 3-arg version forwards to the full overload
    public Task<string> TranscribeAsync(Stream audio, string fileName, string contentType)
        => TranscribeAsync(audio, fileName, contentType, null, null);

    // 5-arg version that accepts language + prompt hints
    public async Task<string> TranscribeAsync(Stream audio, string fileName, string contentType, string? language, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(_key))
            throw new InvalidOperationException("OpenAI:ApiKey missing.");

        // Build multipart/form-data
        using var form = new MultipartFormDataContent();

        var fileContent = new StreamContent(audio);

        // ===== PATCH: sanitize content-type (strip params like ;codecs=opus) =====
        string safeType = "application/octet-stream";
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var mt = contentType.Split(';')[0].Trim(); // e.g., "audio/webm"
            if (!string.IsNullOrWhiteSpace(mt)) safeType = mt;
        }
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(safeType);
        // =======================================================================

        form.Add(fileContent, "file", string.IsNullOrWhiteSpace(fileName) ? "audio.webm" : fileName);

        form.Add(new StringContent(_model), "model");
        if (!string.IsNullOrWhiteSpace(language)) form.Add(new StringContent(language), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = form;

        using var res = await _http.SendAsync(req);
        var json = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI STT {res.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        // API returns: { "text": "..." }
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        return text ?? string.Empty;
    }
}
// ===== BLOCK: AUDIO_TRANSCRIBER END =====

// ===== BLOCK: TRANSCRIPT_PARSER START =====
public class VoiceParsed
{
    public string? Name { get; set; }
    public string? Unit { get; set; }
    public string? Address { get; set; }
    public string? Contact { get; set; }
    public string? Description { get; set; }
    public string? Lang { get; set; } // "en" or "fr"
}

public interface ITranscriptParser
{
    Task<VoiceParsed> ParseAsync(string transcript);
}

public class OpenAiTranscriptParser : ITranscriptParser
{
    private readonly HttpClient _http;
    private readonly string _key;
    private readonly string _model;

    public OpenAiTranscriptParser(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _key = cfg["OpenAI:ApiKey"] ?? "";
        // Prefer a dedicated parser model; falls back to the general model
        _model = cfg["OpenAI:ParserModel"] ?? cfg["OpenAI:Model"] ?? "gpt-4o-mini";
    }

    public async Task<VoiceParsed> ParseAsync(string transcript)
    {
        if (string.IsNullOrWhiteSpace(_key))
            throw new InvalidOperationException("OpenAI:ApiKey missing.");

        // ===== PATCH: normalize to non-null once to silence CS8604s =====
        transcript ??= string.Empty;
        // =================================================================

        var payload = new
        {
            model = _model,
            temperature = 0,
            messages = new object[]
            {
                new {
                    role = "system",
                    content =
@"Extract structured fields from a single voicemail transcription. Caller may speak EN or FR (including Québec French).
Return ONLY a JSON object with these keys:
{
  ""name"": string|null,          // person name if stated
  ""unit"": string|null,          // apt/unit like ""appartement 300"", ""3B"", ""#12""
  ""address"": string|null,       // street address if present
  ""contact"": string|null,       // phone or email
  ""description"": string|null,   // maintenance problem only (may be ignored by server)
  ""lang"": ""en""|""fr""          // best guess
}

Rules:
- Do NOT invent data. Use null if a field is missing.
- The NAME ends BEFORE any unit/address mention. If you see ""à l’appartement|unité|apt|# …"", the part after that is NOT the name.
- FR phrasing like ""sur 257 Dolwich"" or ""au 257 Dolwich"" indicates an address ""257 Dolwich"".
- Keep description short to the issue only; the server may store the full transcript instead.
- Trim honorifics (Mr./Mme) from name.

Examples:
EN: ""Hi I'm Alex in unit 3B, phone 514-555-0101. My sink is leaking under the cabinet.""
→ {""name"":""Alex"",""unit"":""3B"",""address"":null,""contact"":""514-555-0101"",""description"":""sink is leaking under the cabinet"",""lang"":""en""}

FR: ""Salut, je m’appelle Marie, appartement 402 au 1200 Sherbrooke Ouest. Le frigo ne refroidit plus.""
→ {""name"":""Marie"",""unit"":""appartement 402"",""address"":""1200 Sherbrooke Ouest"",""contact"":null,""description"":""le frigo ne refroidit plus"",""lang"":""fr""}

FR-QC: ""C’est Kevin à l’appartement 35 sur 257 Dolwich, j’ai un problème avec mon robinet.""
→ {""name"":""Kevin"",""unit"":""appartement 35"",""address"":""257 Dolwich"",""contact"":null,""description"":""problème avec mon robinet"",""lang"":""fr""}"
                },
                new { role = "user", content = transcript ?? string.Empty }
            }
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI parser {res.StatusCode}: {json}");

            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            // Extract the inner { ... } to be safe
            var s = text.Trim();
            var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
            if (i >= 0 && j > i) s = s.Substring(i, j - i + 1);

            var parsed = JsonSerializer.Deserialize<VoiceParsed>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return PostCleanOrFallback(parsed, transcript);
        }
        catch
        {
            return RegexFallback(transcript);
        }
    }

    // Post-clean AI output + salvage missing fields from transcript
    private static VoiceParsed PostCleanOrFallback(VoiceParsed? p, string transcript)
    {
        if (p == null) return RegexFallback(transcript);

        string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        // work on a guaranteed non-null source string
        var src = transcript ?? string.Empty;

        var cleaned = new VoiceParsed
        {
            Name = N(p.Name),
            Unit = N(p.Unit),
            Address = N(p.Address),
            Contact = N(p.Contact),
            // ALWAYS keep full message for description (server behavior)
            Description = src.Trim(),
            Lang = (p.Lang == "fr" || p.Lang == "en") ? p.Lang : null
        };

        // 1) if Name accidentally includes a unit/address tail, strip it
        if (!string.IsNullOrWhiteSpace(cleaned.Name))
        {
            var cut = cleaned.Name;

            // strip tails like "à l'appartement 35 ..." or "in unit 3B ..."
            cut = Regex.Replace(cut, @"\s*(?:à|au|a)\s+l['’]?(?:appartement|unité)\s*\d+[A-Za-z\-]*.*$", "", RegexOptions.IgnoreCase);
            cut = Regex.Replace(cut, @"\s*(?:in|at)\s+(?:unit|apt|apartment|suite|#)\s*\S+.*$", "", RegexOptions.IgnoreCase);
            // also if someone says: "Robert at 257 Dulwich" treat only "Robert"
            cut = Regex.Replace(cut, @"\s+(?:at|au|à)\s+\d{1,5}\s+.*$", "", RegexOptions.IgnoreCase);

            cut = cut.Trim();

            // nuke name if it looks like a sentence, contains schedule words, or too long / digits
            var looksBad =
                cut.Length > 40 ||
                Regex.IsMatch(cut, @"\d") ||
                Regex.IsMatch(cut.ToLowerInvariant(), @"\b(appartement|unité|apt|unit|apartment|rue|avenue|boulevard|chemin|journ[eé]e|maison|demain|matin|soir)\b");

            cleaned.Name = looksBad ? null : cut;
        }

        // 2) Salvage pattern: "<unit> (sur|au|à|at) <number street>"
        var reUnitAddr = new Regex(
            @"(?:appartement|unité|apt|apartment|suite|unit|#)\s*([A-Za-z0-9\-]+)\s+(?:sur|au|à|at)\s+(\d{1,5}\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*(?:\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*)*)(?=,|\.|\s+et\b|$)",
            RegexOptions.IgnoreCase);
        var mUA = reUnitAddr.Match(src);
        if (mUA.Success)
        {
            var unitGuess = "appartement " + mUA.Groups[1].Value.Trim();
            var addrGuess = mUA.Groups[2].Value.Trim();

            if (string.IsNullOrWhiteSpace(cleaned.Unit))
                cleaned.Unit = unitGuess;

            if (string.IsNullOrWhiteSpace(cleaned.Address) ||
                cleaned.Address!.Length < 6 ||
                Regex.IsMatch(cleaned.Address!, @"\bsur\b", RegexOptions.IgnoreCase))
            {
                cleaned.Address = addrGuess;
            }
        }

        // 2b) FR-QC ordinal floors → unit (e.g., "au deuxième" -> appartement 2)
        if (string.IsNullOrWhiteSpace(cleaned.Unit))
        {
            string lower = src.ToLowerInvariant();
            (string pat, string unitVal)[] ords = new[]
            {
                (@"\bau\s+(?:1er|1re|premier|première)\b", "appartement 1"),
                (@"\bau\s+(?:2e|2eme|2ème|deuxieme|deuxième)\b", "appartement 2"),
                (@"\bau\s+(?:3e|3eme|3ème|troisieme|troisième)\b", "appartement 3"),
                (@"\bau\s+(?:4e|4eme|4ème|quatrieme|quatrième)\b", "appartement 4"),
                (@"\bau\s+(?:5e|5eme|5ème|cinquieme|cinquième)\b", "appartement 5"),
                (@"\bau\s+(?:rdc|rez[\s\-]de[\s\-]chauss[ée]e)\b", "appartement RDC")
            };
            foreach (var (pat, val) in ords)
            {
                if (Regex.IsMatch(lower, pat, RegexOptions.IgnoreCase))
                {
                    cleaned.Unit = val;
                    break;
                }
            }
        }

        // 3) If unit still missing, a generic capture
        if (string.IsNullOrWhiteSpace(cleaned.Unit))
        {
            var mUnit = Regex.Match(src,
                @"\b(?:(?:apt|apartment|suite|unit|#|appartement|unité)\s*[A-Za-z0-9\-]+)",
                RegexOptions.IgnoreCase);
            if (mUnit.Success) cleaned.Unit = mUnit.Value.Trim();
        }

        // 4) Address salvage if weak
        bool addrWeak = string.IsNullOrWhiteSpace(cleaned.Address) || cleaned.Address!.Trim().Length < 6;
        if (addrWeak)
        {
            var mAddr1 = Regex.Match(src,
                @"\b(?:à|au|sur|at)\s+(\d{1,5}\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*(?:\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*)*)",
                RegexOptions.IgnoreCase);
            if (mAddr1.Success) cleaned.Address = mAddr1.Groups[1].Value.Trim();

            if (string.IsNullOrWhiteSpace(cleaned.Address))
            {
                var mAddr2 = Regex.Match(src,
                    @"\b\d{1,5}\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*(?:\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*)*",
                    RegexOptions.IgnoreCase);
                if (mAddr2.Success) cleaned.Address = mAddr2.Value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(cleaned.Address))
                cleaned.Address = Regex.Replace(cleaned.Address!, @"\s*[\,\.;].*$", "");
        }

        return cleaned;
    }

    // Strong FR/EN regex fallback
    private static VoiceParsed RegexFallback(string transcript)
    {
        var t = transcript ?? "";
        string? name = null, unit = null, address = null, contact = null, lang = null;

        if (Regex.IsMatch(t, @"\b(je|moi|mon|c['’]est|ici|à|au|appartement|unité)\b", RegexOptions.IgnoreCase))
            lang = "fr";
        else if (!string.IsNullOrWhiteSpace(t))
            lang = "en";

        // Contact
        var rxPhone = new Regex(@"\+?1?[\s\-.]?\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4}", RegexOptions.IgnoreCase);
        var mPhone = rxPhone.Match(t);
        if (mPhone.Success) contact = mPhone.Value.Trim();
        if (contact == null)
        {
            var rxEmail = new Regex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
            var mEmail = rxEmail.Match(t);
            if (mEmail.Success) contact = mEmail.Value.Trim();
        }

        // Name before unit phrase
        var mName = Regex.Match(t,
            @"(?:(?:my name is|i am|i'm|je m'appelle|mon nom c['’]est|moi c['’]est|c['’]est|ici)\s+)([A-Za-zÀ-ÖØ-öø-ÿ'’\- ]{2,60}?)(?=\s+(?:à|au|a)\s+l['’]?(?:appartement|unité)\b|(?:\s+in\s+|\s+at\s+)(?:unit|apt|apartment|suite|#)\b|[\.,]|$)",
            RegexOptions.IgnoreCase);
        if (mName.Success)
        {
            var raw = mName.Groups[1].Value.Trim();
            if (!Regex.IsMatch(raw, @"\d")) name = raw;
        }
        if (name is null)
        {
            var mName2 = Regex.Match(t,
                @"(?:(?:my name is|i am|i'm|je m'appelle|mon nom c['’]est|moi c['’]est|c['’]est|ici)\s+)([A-Za-zÀ-ÖØ-öø-ÿ'’\- ]{2,60})",
                RegexOptions.IgnoreCase);
            if (mName2.Success)
            {
                var raw = mName2.Groups[1].Value.Trim();
                raw = Regex.Replace(raw, @"\s*(?:à|au|a)\s+l['’]?(?:appartement|unité)\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
                if (!Regex.IsMatch(raw, @"\d")) name = raw;
            }
        }

        // Unit (generic)
        var mUnit = Regex.Match(t, @"\b(?:(?:apt|apartment|suite|unit|#|appartement|unité)\s*[A-Za-z0-9\-]+)", RegexOptions.IgnoreCase);
        if (mUnit.Success) unit = mUnit.Value.Trim();

        // Ordinals → unit
        if (string.IsNullOrWhiteSpace(unit))
        {
            string lower = t.ToLowerInvariant();
            (string pat, string val)[] ords = new[]
            {
                (@"\bau\s+(?:1er|1re|premier|première)\b", "appartement 1"),
                (@"\bau\s+(?:2e|2eme|2ème|deuxieme|deuxième)\b", "appartement 2"),
                (@"\bau\s+(?:3e|3eme|3ème|troisieme|troisième)\b", "appartement 3"),
                (@"\bau\s+(?:4e|4eme|4ème|quatrieme|quatrième)\b", "appartement 4"),
                (@"\bau\s+(?:5e|5eme|5ème|cinquieme|cinquième)\b", "appartement 5"),
                (@"\bau\s+(?:rdc|rez[\s\-]de[\s\-]chauss[ée]e)\b", "appartement RDC")
            };
            foreach (var (pat, val) in ords)
            {
                if (Regex.IsMatch(lower, pat, RegexOptions.IgnoreCase)) { unit = val; break; }
            }
        }

        // Address
        var mAddr1 = Regex.Match(t,
            @"\b(?:à|au|sur|at)\s+(\d{1,5}\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*(?:\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*)*)",
            RegexOptions.IgnoreCase);
        if (mAddr1.Success) address = mAddr1.Groups[1].Value.Trim();
        if (address is null)
        {
            var mAddr2 = Regex.Match(t,
                @"\b\d{1,5}\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*(?:\s+[A-Za-zÀ-ÖØ-öø-ÿ][\wÀ-ÖØ-öø-ÿ'’\-]*)*",
                RegexOptions.IgnoreCase);
            if (mAddr2.Success) address = mAddr2.Value.Trim();
        }
        if (!string.IsNullOrWhiteSpace(address))
            address = Regex.Replace(address!, @"\s*[\,\.;].*$", "");

        // Description = FULL transcript
        var description = t.Trim();

        return new VoiceParsed
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
            Unit = string.IsNullOrWhiteSpace(unit) ? null : unit,
            Address = string.IsNullOrWhiteSpace(address) ? null : address,
            Contact = string.IsNullOrWhiteSpace(contact) ? null : contact,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Lang = lang
        };
    }
}
// ===== BLOCK: TRANSCRIPT_PARSER END =====
