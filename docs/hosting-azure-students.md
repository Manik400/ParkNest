# Hosting ParkNest on Azure for Students + Neon

**Written:** 2026-09-14. Azure's portal moves buttons around; if a label below differs slightly, look
for the nearest match. Prices are Microsoft's published rates on this date.

Why this host: Azure for Students gives **$100 of credit for 12 months with no card**, verified by a
college email. The app runs continuously (it never sleeps), which the free no-card hosts could not
promise ([hosting-back4app-neon.md](hosting-back4app-neon.md) shows a 60-minute temporary URL).

| Piece | Where | Cost |
|---|---|---|
| API + admin site | Azure Container Apps, Consumption plan, 0.25 vCPU / 0.5 GiB, 1 replica always on | Mostly covered by the monthly free grant; a few dollars a month above it |
| Container image | Azure Container Registry, Basic | About $0.167 a day, roughly $5 a month |
| Build and deploy | GitHub Actions, created by the portal | Free minutes |
| Postgres + PostGIS | Neon, free tier (already set up) | $0 |
| Sign-in codes | Gmail SMTP | $0 |
| Payments | Cashfree sandbox | $0 |

**Expected spend:** roughly **$5–15 a month** in total, depending on traffic, so the $100 lasts
most of the year. Step 7 sets an alert so it never surprises you.

The Container Apps free grant each month is 180,000 vCPU-seconds, 360,000 GiB-seconds and
2 million requests. With one replica always running, the app uses more than that, but a replica
with no traffic is billed at the reduced *idle* rate, which is what keeps the bill small.

---

## 1. Activate Azure for Students (10 minutes)

1. Open <https://azure.microsoft.com/free/students> and click **Start free**.
2. Sign in with a Microsoft account (create one with any email if needed).
3. Verify with your **college email**. Microsoft sends a code or link there.
4. Accept the offer. No card is asked for. You land in the Azure portal, <https://portal.azure.com>,
   with a subscription called **Azure for Students**.

## 2. Find the regions you are allowed to use (2 minutes)

Student subscriptions can only deploy to about five regions, and the list differs per person.

1. In the portal search bar, type **Policy** and open it.
2. **Assignments** → open the one named like *Allowed resource deployment regions*.
3. Note the **allowed locations**.

Pick one for everything below, in this order of preference:

- **East US 2**, **East US** or **Central US**: closest to the Neon database, which is in AWS
  `us-east-2` (Ohio). Every page makes several database queries, so this matters most.
- Otherwise any allowed region. If only Asian or Indian regions are allowed, the app still
  works, just slower per request; moving the database next to it is a later step.

## 3. Create the container app (10 minutes)

1. Search **Container Apps** → **Create** → **Container App**.
2. **Basics** tab:
   - Subscription: **Azure for Students**.
   - Resource group: **Create new** → `parknest-rg`.
   - Container app name: `parknest`.
   - Deployment source: **Container image**.
   - Region: the one you picked in step 2.
   - Container Apps environment: **Create new**, name `parknest-env`, plan **Consumption only**.
3. **Container** tab: tick **Use quickstart image**. It is a placeholder; step 4 replaces it
   with ParkNest.
4. **Ingress** tab: **Enabled**, traffic **Accepting traffic from anywhere**, ingress type
   **HTTP**, target port **80** for now (the quickstart image listens on 80; step 5 changes it).
5. **Review + create** → **Create**. Wait for "Your deployment is complete" → **Go to resource**.

## 4. Connect GitHub so it builds ParkNest (10 minutes)

1. In the `parknest` app: left menu **Settings** → **Deployment** (older portals call it
   **Continuous deployment**).
2. **Sign in with GitHub** and authorise Azure.
3. Fill in:
   - Organization: `Manik400`. Repository: `ParkNest`. Branch: `14Sept`.
   - Repository source / Dockerfile: **Dockerfile**, path `./Dockerfile`, context `./`.
   - Registry: **Create new** Azure Container Registry, SKU **Basic**, same region and
     resource group. Name something like `parknestacr` plus a few digits (it must be globally
     unique, lowercase letters and digits only).
   - Authentication: **User-assigned identity** or **Service principal**; either works. Let
     the portal create it.
4. **Start continuous deployment.**

What this does: Azure commits a workflow file to `.github/workflows/` on `14Sept` in your repo.
GitHub Actions builds the image from the [`Dockerfile`](../Dockerfile) (about 10 minutes the first
time), pushes it to the registry, and updates the app. Every later push to `14Sept` redeploys.
Watch it on GitHub → your repo → **Actions**.

The first run will fail at start-up until step 5 is done: without the database and secrets the
API refuses to start. That is expected.

## 5. Settings: port, secrets, always-on (10 minutes)

**Ingress.** Left menu **Ingress** → **Target port** `8080` → **Save**. (The Dockerfile's
`EXPOSE 8080`; Kestrel in the .NET image listens there.)

**Secrets.** Left menu **Settings → Secrets** → **Add**, one per row. Secret names must be
lowercase letters, digits and `-`:

| Secret name | Value |
|---|---|
| `db-connection` | Neon connection string. Either `postgresql://…` as Neon shows it, or `Host=…;Database=…` |
| `signing-key` | `AUTH__SIGNINGKEY` from `.env.back4app` |
| `otp-pepper` | `AUTH__OTPPEPPER` from `.env.back4app` |
| `kyc-pepper` | `KYC__PEPPER` from `.env.back4app` |
| `gmail-app-password` | Your 16-character Gmail App Password |

**Environment variables.** Left menu **Application → Containers** (or **Revisions and
replicas → Edit and deploy**) → select the container → **Environment variables** tab. Add:

| Name | Source | Value |
|---|---|---|
| `CONNECTIONSTRINGS__PARKNEST` | Reference a secret | `db-connection` |
| `AUTH__SIGNINGKEY` | Reference a secret | `signing-key` |
| `AUTH__OTPPEPPER` | Reference a secret | `otp-pepper` |
| `KYC__PEPPER` | Reference a secret | `kyc-pepper` |
| `EMAIL__PASSWORD` | Reference a secret | `gmail-app-password` |
| `AUTH__ADMINEMAILS__0` | Manual entry | your email |
| `EMAIL__USERNAME` | Manual entry | your Gmail address |

Everything else is already in the image: the [`Dockerfile`](../Dockerfile) sets
`ASPNETCORE_ENVIRONMENT=Staging` and trusts the proxy's forwarded headers, and
[`appsettings.Staging.json`](../src/ParkNest.Api/appsettings.Staging.json) turns on SMTP, the
memory cache and migrations at startup.

**Size and scale**, same editing screen:

- CPU and memory: **0.25 vCPU, 0.5 Gi**.
- Scale → **Min replicas 1, Max replicas 1.**
  - Min 1 keeps it always on. Min 0 would sleep between visits and make the first request slow.
  - Max 1 is required, not just cheap: the API applies database migrations at startup, which is
    safe only when exactly one copy starts.

**Save / Create** → a new revision starts. Left menu **Revisions and replicas** should show it
*Running*, and **Log stream** should end with:

```
Applying N pending migration(s)
Hosting environment: Staging
Now listening on: http://[::]:8080
```

## 6. Open it (2 minutes)

1. **Overview** → **Application Url**, like `https://parknest.<words>.<region>.azurecontainerapps.io`.
2. `<url>/health` → `{"status":"ok"}`.
3. `<url>/` → the ParkNest sign-in page. Sign in with the admin email; the code arrives in Gmail.

## 7. Budget alert (2 minutes)

Search **Cost Management** → **Budgets** → **Add**. Scope: the Azure for Students subscription.
Amount **$15** a month, alert at 80%, your email. Azure then emails you before the credit
drains. Student subscriptions stop (they are never charged to a card) when the credit runs out.

## 8. Turn on Cashfree

Add as environment variables (the secret the same way as above, name `cashfree-secret`), then
save:

```
PAYMENTS__PROVIDER=Cashfree
PAYMENTS__PUBLICBASEURL=<Application Url, no trailing slash>
PAYMENTS__RETURNURLS__ADMIN=<Application Url>/wallet
PAYMENTS__CASHFREE__CLIENTID=<Cashfree test App ID>
PAYMENTS__CASHFREE__CLIENTSECRET=secretref:cashfree-secret
PAYMENTS__CASHFREE__PAYMENTMETHODS=upi
```

Test: Wallet → Add credits → Net Banking → any bank → OTP `111000` → SUCCESS. Optional webhook in
the Cashfree dashboard: `<Application Url>/api/payments/webhook`, events *Payment success* and
*Payment failed*, version `2026-01-01`.

## 9. Mobile app

```bash
flutter build apk --dart-define=PARKNEST_API_BASE_URL=<Application Url>
```

## If something goes wrong

- **"RequestDisallowedByAzure" / "disallowed by policy"** when creating anything: the region is
  not on your allowed list (step 2). Pick an allowed one for every resource.
- **Registry name taken**: add more digits.
- **GitHub Actions run fails**: open it on GitHub → Actions, copy the red step's last lines.
- **Revision fails to start**: **Log stream** or **Monitoring → Logs**. "Couldn't set …",
  "No sign-in channel is configured" and "is not configured" each name the variable to fix.
- **Blank page**: fixed in commit `8301f0b`; make sure `14Sept` includes it.

## Limits to know

- **Photos reset on every redeploy.** `Storage:Provider=Local` writes to the container's disk.
  Azure Blob Storage is the natural next step when listing photos matter.
- **One replica.** See the scale note in step 5.
- **`/metrics` is public** on this setup.
- **After 12 months** the student credit ends; renew it if you are still a student, or move to
  pay-as-you-go.
