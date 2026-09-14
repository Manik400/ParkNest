# Hosting ParkNest on Back4App + Neon, for ₹0 and no card

**Written:** 2026-09-12. Free tiers change; check the provider's pricing page if anything below looks off.

Why Back4App: of the hosts checked on this date, it is the one that runs a Docker app from GitHub
for free **without asking for a card**.

| Host | Card needed? | Why not |
|---|---|---|
| Render | Yes, even for the free instance (seen at signup, 2026-09-12) | Card |
| Hugging Face Spaces | Docker Spaces need a paid PRO plan to create | Paid |
| Koyeb | Its pricing FAQ says a card is required | Card |
| Railway | No card for a 30-day, $5 trial; card after that | Temporary |
| Fly.io, Google Cloud Run, Oracle Cloud | Card at signup | Card |
| **Back4App Containers** | **No** | 256 MB RAM, 0.25 CPU |

**Will ParkNest fit in 256 MB?** Measured locally with the image's settings (Release build, admin
site in `wwwroot`, workstation GC, `DOTNET_GCConserveMemory=5`): about **150 MB working set under
load, 48 MB private**. It fits, with room. The Dockerfile sets those GC options.

One Back4App container runs the API with the admin site built into it (from the
[`Dockerfile`](../Dockerfile)); Neon holds the database. The mobile app points at the same URL.

## 1. Database: Neon

1. In the Neon project, **SQL Editor**, run once:

   ```sql
   CREATE EXTENSION IF NOT EXISTS postgis;
   CREATE EXTENSION IF NOT EXISTS btree_gist;
   ```

2. **Connect**, with connection pooling **off** (the host must not contain `-pooler`; migrations
   need a direct connection). Shape it for .NET:

   ```
   Host=ep-xxxx.region.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=…;SSL Mode=Require;Trust Server Certificate=true
   ```

## 2. First deploy: payments off

The Cashfree settings need the app's own public URL, which Back4App only shows after the first
deploy. So the first deploy runs with payments switched off, and the second turns them on.

1. Sign up at <https://www.back4app.com> with GitHub. No card.
2. **Build new app → Containers as a Service**. Give it access to the `ParkNest` repository.
3. Pick the repository, branch `claude/payment-gateway-setup-1cba14`, root directory `./`
   (the Dockerfile is at the repo root). Port **8080** (the Dockerfile's `EXPOSE 8080`; enter it
   if the form asks).
4. Environment variables. **Back4App accepts names in UPPERCASE letters, digits and underscores
   only**, so the names below are uppercase; .NET reads configuration case-insensitively, so they
   are the same settings. Everything that is not a secret is baked into the image (the
   [`Dockerfile`](../Dockerfile) sets `ASPNETCORE_ENVIRONMENT=Staging`, and
   [`appsettings.Staging.json`](../src/ParkNest.Api/appsettings.Staging.json) turns on SMTP,
   the memory cache and migrations at startup), so only these seven are added by hand.

   Generate the three random values on Windows, one run each:

   ```bash
   powershell -Command "[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])"
   ```

   ```
   CONNECTIONSTRINGS__PARKNEST=<Neon direct connection string>
   AUTH__SIGNINGKEY=<random value 1>
   AUTH__OTPPEPPER=<random value 2>
   KYC__PEPPER=<random value 3>
   AUTH__ADMINEMAILS__0=<your email>
   EMAIL__USERNAME=<your gmail address>
   EMAIL__PASSWORD=<16-character Gmail App Password>
   ```

   The Gmail settings are required, not optional. Outside Development the API never registers the
   development sender that returns the code in the response, so without SMTP there is no way to
   sign in and startup stops with "No sign-in channel is configured". Mail is sent from the Gmail
   address itself; set `EMAIL__FROMADDRESS` only to use a different sender.

   Health check path: `/health`.
5. Deploy. The first build takes about 10 minutes (Angular, then .NET). The log should end with
   "Applying N pending migration(s)" and "Now listening on: http://[::]:8080".
6. Copy the app URL Back4App shows. Open `<url>/health` → `{"status":"ok"}`, then `<url>/`, which
   is the admin sign-in page. Sign in with your email; that account is the admin.

## 3. Second deploy: Cashfree on

In the app's settings, add these six, then redeploy:

```
PAYMENTS__PROVIDER=Cashfree
PAYMENTS__PUBLICBASEURL=<app URL, https, no trailing slash>
PAYMENTS__RETURNURLS__ADMIN=<app URL>/wallet
PAYMENTS__CASHFREE__CLIENTID=<Cashfree test App ID>
PAYMENTS__CASHFREE__CLIENTSECRET=<Cashfree test secret>
PAYMENTS__CASHFREE__PAYMENTMETHODS=upi
```

Then Wallet → Add credits opens Cashfree's sandbox page. For a quick test pay with Net Banking →
any bank, OTP `111000`, choose SUCCESS. No money moves.

**Optional webhook.** The app URL is public https, which Cashfree requires. Dashboard →
Developers → Webhooks → Payment Gateway: `<app URL>/api/payments/webhook`, events *Payment
success* and *Payment failed*, version `2026-01-01`, no secret. Credits land without it too,
because the API asks Cashfree when the browser comes back.

## 4. Mobile app

```bash
flutter build apk --dart-define=PARKNEST_API_BASE_URL=<app URL>
```

## Limits to know

- **256 MB RAM, 0.25 CPU.** Enough for a pilot: measured about 150 MB. Slow under real load.
- **Sleep and monthly hours** are not stated on Back4App's pricing page as of this date. Watch
  the dashboard; a first request after idle may be slow.
- **Photos do not survive a redeploy.** `Storage:Provider=Local` writes to the container disk.
  Move to object storage (Cloudflare R2 has a free tier) when listing photos matter.
- **One instance only.** `Database__MigrateOnStartup=true` is safe because exactly one runs.
- **`/metrics` is public** on this setup. Treat those counters as public until it is firewalled.

## Going live later

Production needs Cashfree KYC and live keys (`PAYMENTS__MODE=Live`, `cfsk_ma_prod_…`),
`ASPNETCORE_ENVIRONMENT=Production`, a current account in the business name, and the policy pages
Cashfree asks for. Read [payment-gateway-setup-fully-free-rnd.md §7](payment-gateway-setup-fully-free-rnd.md#7-legal-and-compliance-notes-read-before-going-live)
first. [hosting-render-neon.md](hosting-render-neon.md) describes the same setup on Render, for
when a card on file is acceptable.
