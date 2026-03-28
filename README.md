# CEObot

CEObot is an ASP.NET Core 8 maintenance-intake app for residential operations.
It lets tenants submit maintenance issues from a web form or a voice recording, stores tickets in SQLite, classifies issues with OpenAI, and gives staff and admins separate workflows for triage and follow-up.

## What the program does

- Tenant intake from the browser at `/tenant.html`
- Browser voice intake at `/voice.html`
- Optional Twilio voicemail intake through `/api/voice/twilio/*`
- Ticket storage in SQLite (`ceobot.db` by default)
- AI-based classification into category and priority
- Staff workflow for claiming and completing tickets
- Admin workflow for ticket assignment, staff management, and dashboards
- Account registration, email verification, JWT cookie auth, and notification preferences
- Swagger docs in Development at `/swagger`

## Tech stack

- .NET 8
- ASP.NET Core minimal APIs
- Entity Framework Core with SQLite
- OpenAI for ticket classification, speech-to-text, and transcript parsing
- Optional Twilio for phone voicemail intake
- Optional SMTP for real email delivery

## Prerequisites

- .NET 8 SDK
- An OpenAI API key if you want AI classification and voice transcription
- Optional Twilio credentials for phone-call intake
- Optional SMTP settings for real email delivery

## Configuration

`appsettings*.json` is intentionally ignored in git, so local secrets should be set with user secrets.

From the project root:

```powershell
dotnet user-secrets set "Admin:Key" "change-this-admin-key"
dotnet user-secrets set "Jwt:SigningKey" "change-this-jwt-signing-key-to-32-plus-chars"
dotnet user-secrets set "PublicBaseUrl" "https://localhost:7175"
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
dotnet user-secrets set "OpenAI:Model" "gpt-4o-mini"
dotnet user-secrets set "OpenAI:TranscribeModel" "gpt-4o-mini-transcribe"
dotnet user-secrets set "OpenAI:ParserModel" "gpt-4o-mini"
```

Useful optional settings:

```powershell
dotnet user-secrets set "ConnectionStrings:db" "Data Source=ceobot.db"
dotnet user-secrets set "Cors:AllowedOrigins:0" "https://localhost:7175"

dotnet user-secrets set "Smtp:Host" "smtp.example.com"
dotnet user-secrets set "Smtp:Port" "587"
dotnet user-secrets set "Smtp:User" "user@example.com"
dotnet user-secrets set "Smtp:Pass" "password"
dotnet user-secrets set "Smtp:From" "no-reply@example.com"
dotnet user-secrets set "Smtp:UseSsl" "true"

dotnet user-secrets set "Twilio:AccountSid" "AC..."
dotnet user-secrets set "Twilio:AuthToken" "..."
dotnet user-secrets set "Twilio:Number" "+15555555555"
dotnet user-secrets set "Voice:Language" "fr-CA"
dotnet user-secrets set "Voice:VoiceName" "Polly.Celine"
```

Notes:

- If `ConnectionStrings:db` is not set, the app uses `ceobot.db` in the project folder.
- If SMTP is not configured, development emails are written to `wwwroot/_outbox/`.
- If `OpenAI:ApiKey` is missing, plain tenant ticket submission still works with fallback values, but AI classification and voice features will be limited or fail.
- The admin dashboard and most admin ticket/staff endpoints currently still depend on `Admin:Key`.

## Start the app

Use the HTTPS launch profile. The account system writes a `Secure` auth cookie, so HTTP-only startup is not enough for login-based flows.

```powershell
dotnet restore
dotnet run --launch-profile https
```

Default development URLs from `launchSettings.json`:

- `https://localhost:7175`
- `http://localhost:5116`

On first run the app creates the SQLite database automatically.

## First-run checklist

1. Start the app with `dotnet run --launch-profile https`.
2. Open `https://localhost:7175/`.
3. Open `/admin.html` and log in with the `Admin:Key` you configured.
4. Create staff members in the admin UI. Each staff member gets a numeric `Id` and an `AccessKey`.
5. Open `/staff.html` and test staff access with that `Id` and `AccessKey`.
6. Open `/tenant.html` to submit a manual maintenance issue.
7. Open `/voice.html` to test browser-based voice intake.
8. Open `/auth.html` if you want to test the newer account registration, verification, login, and notification-preference flow.

Important auth note:

- `/auth.html` uses JWT cookies for user accounts.
- `/admin.html` and several admin APIs still use the header-based `Admin:Key` path.
- Staff access currently uses `Staff ID + Access Key`, not JWT account login.

## Main pages

- `/` home page
- `/tenant.html` tenant form
- `/voice.html` browser voice intake
- `/staff.html` staff portal
- `/admin.html` admin dashboard
- `/auth.html` account registration and login
- `/debug.html` self-test page
- `/swagger` API explorer in Development

## Voice and telephony

There are two voice paths:

- Browser microphone upload through `/voice.html`
- Twilio voicemail webhook flow through `/api/voice/twilio/inbound` and `/api/voice/twilio/recording-complete`

If you use Twilio locally, set `PublicBaseUrl` to a public HTTPS URL and call `/api/voice/twilio/sync-webhook` with `X-Admin-Key` to update the webhook target.

## Project structure

- `Program.cs`: main API, ticket logic, AI integration, models
- `Accounts.cs`: account registration, JWT auth, account admin endpoints
- `Security.cs`: CORS, forwarded headers, admin gate, rate limiting
- `VoiceTelephony.cs`: Twilio voicemail flow
- `Emailing.cs`: SMTP sender and dev email outbox
- `wwwroot/`: static frontend pages

## Validation

Local build command:

```powershell
dotnet build
```
