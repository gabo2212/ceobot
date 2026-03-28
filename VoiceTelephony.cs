// VoiceTelephony.cs
// Minimal, isolated Twilio voicemail intake that feeds your existing voice pipeline.

using System.Text;
using System.Text.RegularExpressions;   // regex for heuristic + phone cleanup
using System.Text.Json;                // small JSON bodies
using System.Net.Http.Headers;         // Basic auth header
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Twilio.TwiML;
using Twilio.TwiML.Voice;

public static class VoiceTelephony
{
    public static IEndpointRouteBuilder MapVoiceTelephony(this IEndpointRouteBuilder app)
    {
        // Inbound call handler: greet + record, then Twilio calls back with recording details.
        app.MapPost("/api/voice/twilio/inbound", (HttpRequest req, IConfiguration cfg)
            => BuildTwilioInboundResponse(req, cfg));

        // Serve TwiML on GET too (prevents redirect loops in browsers/providers).
        app.MapGet("/api/voice/twilio/inbound", (HttpRequest req, IConfiguration cfg)
            => BuildTwilioInboundResponse(req, cfg));

        // Recording callback: Twilio POSTs recording + caller info; we create a ticket.
        app.MapPost("/api/voice/twilio/recording-complete", async (
            HttpRequest req, IConfiguration cfg, AppDb db,
            IAudioTranscriber stt, ITranscriptParser parser, IAIClassifier ai,
            IEmailSender emailSender, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("VoiceTelephony");
            var form = await req.ReadFormAsync();

            string recordingUrl = form["RecordingUrl"];
            string recordingSid = form["RecordingSid"];
            string callSid = form["CallSid"];
            string from = form["From"]; // may be empty in this callback
            string to = form["To"];

            if (string.IsNullOrWhiteSpace(recordingUrl))
                return Results.BadRequest(new { error = "RecordingUrl missing" });

            var accountSid = cfg["Twilio:AccountSid"];
            var authToken = cfg["Twilio:AuthToken"];
            if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(authToken))
                return Results.BadRequest(new { error = "Twilio credentials missing" });

            // If Twilio didn't send From here, fetch it via Calls API by CallSid.
            if (string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(callSid))
            {
                var fetched = await FetchCallerFromCallSidAsync(accountSid!, authToken!, callSid!);
                if (!string.IsNullOrWhiteSpace(fetched)) from = fetched!;
            }

            // Download the MP3 (Twilio protects recordings with Basic auth).
            var mediaUrl = recordingUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? recordingUrl : recordingUrl + ".mp3";

            byte[] audioBytes;
            try
            {
                using var http = new HttpClient();
                var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
                audioBytes = await http.GetByteArrayAsync(mediaUrl);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "failed to download recording", mediaUrl, ex = ex.Message });
            }

            // Transcribe using your existing STT client (same vocab bias as voice page).
            const string ASR_HINT_QC = @"
Contexte: appels de maintenance résidentielle (FR/EN, accents du Québec).
Termes fréquents: frigidaire, poêle, four, prise, disjoncteur/breaker, panneau électrique, chauffe-eau,
robinet, toilette, évier, lavabo, douche, drain, fuite, dégât d’eau, laveuse, sécheuse,
thermostat, chauffage, air climatisé, clim, HVAC, serrure, poignée, porte d’entrée,
wifi, internet, modem, routeur, insectes, coquerelles, punaises, souris, bruit, plancher, mur.
Noms & unités: “Je m’appelle”, “mon nom c’est”, “j’habite au”, “appartement”, “unité”, “#”.
";
            string transcript;
            using (var ms = new MemoryStream(audioBytes))
            {
                transcript = await stt.TranscribeAsync(ms, $"{recordingSid}.mp3", "audio/mpeg", null, ASR_HINT_QC);
            }
            if (string.IsNullOrWhiteSpace(transcript))
                return Results.BadRequest(new { error = "empty transcription", recordingSid, callSid });

            // Parse → classify (full transcript saved as Description)
            var p = await parser.ParseAsync(transcript);
            p.Description = transcript.Trim();

            IssueCategory cat = IssueCategory.Other;
            Priority pri = Priority.Normal;
            try { (cat, pri, _) = await ai.ClassifyAsync(transcript); } catch { }

            // Build contact: always include the caller number if we have it.
            var parsedContact = string.IsNullOrWhiteSpace(p.Contact) ? null : p.Contact!.Trim();
            var caller = NormalizeCaller(from);
            string? contact = parsedContact ?? caller;
            if (!string.IsNullOrWhiteSpace(parsedContact) && !string.IsNullOrWhiteSpace(caller))
            {
                // If parser contact doesn't already include caller, append it.
                var cleanedParsed = Regex.Replace(parsedContact!, @"[^\d\+]", "");
                var cleanedCaller = Regex.Replace(caller!, @"[^\d\+]", "");
                if (!cleanedParsed.Contains(cleanedCaller))
                    contact = $"{parsedContact} | {caller}";
            }

            var t = new Ticket
            {
                TenantName = string.IsNullOrWhiteSpace(p.Name) ? "(voicemail)" : p.Name!.Trim(),
                Unit = p.Unit?.Trim() ?? p.Address?.Trim(),
                Contact = contact,
                Description = transcript.Trim(),
                Category = cat,
                Priority = pri,
                Status = TicketStatus.Open,
                CreatedUtc = DateTime.UtcNow
            };
            db.Tickets.Add(t);
            await db.SaveChangesAsync();

            // Optional heuristic (same as voice page, but local copy here)
            var (score, reasons) = HeuristicUncertaintyLocal(p, transcript);

            var notifyTargets = await LoadActiveAccountEmailsAsync(db);
            var notificationResults = new List<(string Email, EmailSendResult Result)>();
            if (notifyTargets.Count > 0)
            {
                var subject = $"Ticket #{t.Id}: Voicemail received";
                var body = BuildTelephonyNotificationBody(t, transcript, caller ?? from, to, score, reasons);

                foreach (var email in notifyTargets)
                {
                    EmailSendResult result;
                    try
                    {
                        result = await emailSender.SendAsync(new EmailMessage(email, subject, body));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "NotificationFailed ticket {TicketId} -> {Target}", t.Id, MaskEmailForLog(email));
                        result = new EmailSendResult(false, null, ex.Message);
                    }

                    notificationResults.Add((email, result));

                    if (result.Sent)
                    {
                        logger.LogInformation("NotificationSent ticket {TicketId} -> {Target}", t.Id, MaskEmailForLog(email));
                    }
                    else
                    {
                        logger.LogWarning("NotificationFailed ticket {TicketId} -> {Target} ({Error})", t.Id, MaskEmailForLog(email), result.Error ?? "unknown error");
                    }
                }
            }

            return Results.Ok(new
            {
                id = t.Id,
                from = caller ?? from, // return normalized if we have it
                to,
                callSid,
                recordingSid,
                category = t.Category.ToString(),
                priority = t.Priority.ToString(),
                uncertainty = new { score, reasons },
                needsReview = score >= 0.50,
                notifications = notificationResults.Select(r => new { email = r.Email, sent = r.Result.Sent, devPath = r.Result.DevFilePath, error = r.Result.Error }).ToArray(),
                notificationAttempted = notifyTargets.Count
            });
        });

        // ===== Twilio webhook auto-sync (dev helper) =====
        // POST /api/voice/twilio/sync-webhook
        // Secured by X-Admin-Key. If baseUrl omitted, tries PublicBaseUrl, then ngrok local API.
        app.MapPost("/api/voice/twilio/sync-webhook", async (HttpRequest req, IConfiguration cfg) =>
        {
            // Admin check (same semantics as AdminAuthorized)
            var supplied = req.Headers["X-Admin-Key"].FirstOrDefault()?.Trim()
                           ?? req.Query["key"].FirstOrDefault()?.Trim();
            var configured = cfg["Admin:Key"]?.Trim();
            if (string.IsNullOrWhiteSpace(configured) || !string.Equals(supplied, configured, StringComparison.Ordinal))
                return Results.Unauthorized();

            // Optional payload: { "baseUrl": "https://xxx.ngrok-free.app" }
            string? baseUrl = null;
            try
            {
                if (req.ContentLength > 0)
                {
                    using var doc = await JsonDocument.ParseAsync(req.Body);
                    baseUrl = doc.RootElement.TryGetProperty("baseUrl", out var p) ? p.GetString() : null;
                }
            }
            catch { /* empty or invalid body is fine */ }

            // Fallbacks: PublicBaseUrl, then ngrok local API
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = cfg["PublicBaseUrl"]?.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = await GetNgrokHttpsUrlAsync();

            if (string.IsNullOrWhiteSpace(baseUrl))
                return Results.BadRequest(new { error = "No baseUrl provided, PublicBaseUrl empty, and ngrok not detected." });

            var accountSid = cfg["Twilio:AccountSid"];
            var authToken = cfg["Twilio:AuthToken"];
            var numberE164 = cfg["Twilio:Number"];
            if (string.IsNullOrWhiteSpace(accountSid) || string.IsNullOrWhiteSpace(authToken) || string.IsNullOrWhiteSpace(numberE164))
                return Results.BadRequest(new { error = "Twilio config missing AccountSid/AuthToken/Number" });

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}")));

            // 1) Find the Phone Number SID (PN...) by E.164 number
            var lookupUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/IncomingPhoneNumbers.json?PhoneNumber={Uri.EscapeDataString(numberE164)}";
            var listJson = await http.GetStringAsync(lookupUrl);
            using var listDoc = JsonDocument.Parse(listJson);
            var arr = listDoc.RootElement.GetProperty("incoming_phone_numbers");
            if (arr.GetArrayLength() == 0)
                return Results.BadRequest(new { error = "Twilio number not found via API", number = numberE164 });

            var pnSid = arr[0].GetProperty("sid").GetString();
            if (string.IsNullOrWhiteSpace(pnSid))
                return Results.BadRequest(new { error = "Could not read Phone Number SID" });

            // 2) Update VoiceUrl to the current base
            var voiceUrl = $"{baseUrl}/api/voice/twilio/inbound";
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["VoiceUrl"] = voiceUrl,
                ["VoiceMethod"] = "POST"
            });
            var updateUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/IncomingPhoneNumbers/{pnSid}.json";
            var res = await http.PostAsync(updateUrl, form);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
                return Results.BadRequest(new { error = "Twilio update failed", status = (int)res.StatusCode, body });

            return Results.Ok(new { ok = true, numberSid = pnSid, voiceUrl });
        });

        return app;
    }


    private static IResult BuildTwilioInboundResponse(HttpRequest req, IConfiguration cfg)
    {
        var publicBase = cfg["PublicBaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBase))
            publicBase = $"{req.Scheme}://{req.Host.Value}";

        var lang = cfg["Voice:Language"] ?? "fr-CA";
        var voice = cfg["Voice:VoiceName"] ?? "Polly.Celine";
        var ssml = cfg["Voice:GreetingSsml"];
        var text = cfg["Voice:Greeting"] ?? "Bonjour, vous avez joint la maintenance. Please leave a message after the beep.";
        var audio = cfg["Voice:AudioUrl"];

        var vr = new VoiceResponse();

        if (!string.IsNullOrWhiteSpace(audio))
        {
            if (Uri.TryCreate(audio, UriKind.Absolute, out var absoluteAudio))
            {
                vr.Play(absoluteAudio);
            }
            else
            {
                vr.Play(new Uri($"{publicBase}{audio}"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(ssml))
        {
            vr.Say(ssml, voice: voice, language: lang);
        }
        else
        {
            vr.Say(text, voice: voice, language: lang);
        }

        var recordingCallback = new Uri($"{publicBase}/api/voice/twilio/recording-complete");
        vr.Record(timeout: 4, maxLength: 120, playBeep: true, trim: Record.TrimEnum.TrimSilence,
                  recordingStatusCallback: recordingCallback,
                  recordingStatusCallbackMethod: "POST");
        vr.Say("No recording received. Goodbye.", voice: voice, language: lang);

        return Results.Text(vr.ToString(), "application/xml");
    }

    private static async Task<HashSet<string>> LoadActiveAccountEmailsAsync(AppDb db)
    {
        var emails = await db.UserAccounts.AsNoTracking()
            .Where(a => !a.IsDisabled && a.EmailConfirmed && !string.IsNullOrWhiteSpace(a.Email))
            .Select(a => a.Email!)
            .ToListAsync();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in emails)
        {
            var trimmed = email?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (!EmailValidation.IsValid(trimmed)) continue;
            set.Add(trimmed);
        }

        return set;
    }

    private static string BuildTelephonyNotificationBody(Ticket ticket, string transcript, string? caller, string? toNumber, double uncertaintyScore, IReadOnlyCollection<string> reasons)
    {
        string Format(string? value) => string.IsNullOrWhiteSpace(value) ? "(not provided)" : value.Trim();

        var sb = new StringBuilder();
        sb.AppendLine("New voicemail ticket was created via phone intake.");
        sb.AppendLine();
        sb.AppendLine($"Ticket #: {ticket.Id}");
        sb.AppendLine($"Submitted (UTC): {ticket.CreatedUtc:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Category: {ticket.Category}");
        sb.AppendLine($"Priority: {ticket.Priority}");
        sb.AppendLine();
        sb.AppendLine($"Caller: {Format(caller)}");
        sb.AppendLine($"Twilio Number: {Format(toNumber)}");
        sb.AppendLine($"Contact saved: {Format(ticket.Contact)}");
        sb.AppendLine();
        sb.AppendLine($"Name: {Format(ticket.TenantName)}");
        sb.AppendLine($"Unit/Address: {Format(ticket.Unit)}");
        sb.AppendLine();
        sb.AppendLine("Full transcript:");
        sb.AppendLine(transcript.Trim());
        sb.AppendLine();
        sb.AppendLine($"Uncertainty score: {Math.Clamp(uncertaintyScore, 0, 1):0.00}");
        if (reasons.Count > 0)
        {
            sb.AppendLine("Flags: " + string.Join(", ", reasons));
            sb.AppendLine();
        }

        sb.AppendLine("You can reply to this email to follow up or assign the ticket in CEObot.");

        return sb.ToString();
    }

    private static string MaskEmailForLog(string email)
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

    // === Heuristics and helpers ===

    private static (double score, List<string> reasons) HeuristicUncertaintyLocal(VoiceParsed p, string transcript)
    {
        var reasons = new List<string>();
        double s = 0;

        bool NameBad(string? x)
        {
            if (string.IsNullOrWhiteSpace(x)) return true;
            var v = x.ToLowerInvariant();
            if (v.Length > 40) return true;
            if (Regex.IsMatch(v, @"\d")) return true;
            if (Regex.IsMatch(v, @"\b(appartement|unité|apt|unit|apartment|rue|avenue|boulevard|ch.[ae]min|chemin)\b", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(v, @"\b(demain|aujourd'hui|matin|soir|journ[eé]e|maison)\b", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        if (NameBad(p?.Name)) { s += 0.20; reasons.Add("nameMissingOrSuspect"); }
        if (string.IsNullOrWhiteSpace(p?.Unit)) { s += 0.20; reasons.Add("unitMissing"); }
        if (string.IsNullOrWhiteSpace(p?.Address) || p!.Address!.Trim().Length < 6) { s += 0.30; reasons.Add("addressMissingOrShort"); }
        if (Regex.IsMatch(transcript ?? "", @"\b(sorry|désol[eé]|en fait|actually|correction)\b", RegexOptions.IgnoreCase)) { s += 0.20; reasons.Add("selfCorrectionDetected"); }
        if ((transcript ?? "").Length < 20) { s += 0.10; reasons.Add("veryShortTranscript"); }

        if (s < 0) s = 0; if (s > 1) s = 1;
        return (s, reasons);
    }

    private static string? NormalizeCaller(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;

        var f = from.Trim();
        if (f.StartsWith("client:", StringComparison.OrdinalIgnoreCase)) return null;
        if (Regex.IsMatch(f, @"^(anonymous|restricted|private)$", RegexOptions.IgnoreCase)) return null;

        // Extract digits/+ and normalize to North American E.164 if it's a bare 10-digit.
        var digits = Regex.Replace(f, @"[^\d\+]", "");
        if (!digits.StartsWith("+") && digits.Length == 10) digits = "+1" + digits;
        if (digits.Length < 7) return null; // not useful

        return digits;
    }

    private static async Task<string?> FetchCallerFromCallSidAsync(string accountSid, string authToken, string callSid)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}")));
            var url = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Calls/{callSid}.json";
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("from", out var fromEl))
                return fromEl.GetString();
        }
        catch { /* ignore; we'll just not have a caller number */ }
        return null;
    }

    // Try to read current https ngrok URL from local API (dev helper)
    private static async Task<string?> GetNgrokHttpsUrlAsync()
    {
        try
        {
            using var http = new HttpClient();
            var s = await http.GetStringAsync("http://127.0.0.1:4040/api/tunnels");
            using var doc = JsonDocument.Parse(s);
            foreach (var t in doc.RootElement.GetProperty("tunnels").EnumerateArray())
                if (string.Equals(t.GetProperty("proto").GetString(), "https", StringComparison.OrdinalIgnoreCase))
                    return t.GetProperty("public_url").GetString();
        }
        catch { /* ngrok may not be running */ }
        return null;
    }
}
