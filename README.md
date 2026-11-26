# Escribot

**Escribot** is an Academic IELTS Writing Coach & Authentic Computer-Delivered (Inspera) Exam Simulator built to help writers break past the **Band 6.5 → 7.5/8.0+ plateau**, with private per-user accounts. It runs as one ASP.NET Core app on Azure Container Apps, with Azure Cosmos DB and Blob Storage.

## Core Capabilities

- **Dual Writing Modes**
  - **Editorial Coach Studio**: 5-minute interactive Outline Check (validating Task 1 number-free macro Overviews & Task 2 Thesis/paragraph structure before writing), distraction-free serif/sans editor, and surgical on-demand tools (**Academic Collocations & Hedging**, **Spanish → English L1 Interference Scan**, **Single-Paragraph Check**) with strictly zero ghost-text autocomplete.
  - **Authentic Inspera Exam Simulator**: Faithful Computer-Delivered IELTS replica supporting **20m Task 1**, **40m Task 2**, and **60m Shared-Clock Full Mock Exams** (`Part 1` / `Part 2` switcher tabs, `1:2` weighted overall Writing Band `(T1 + 2 × T2) / 3`, prompt yellow highlighter, candidate scratchpad notes, spellcheck disabled).
- **Full Academic Task 1 & Task 2 Coverage**
  - Pre-seeded library spanning **all 7 Task 1 visual archetypes** (Line Graph, Bar Chart, Comparative Pie Charts, Data Table, Mixed Dual-Axis Charts, Process Diagram, Two-Period Comparison Map) and **all 5 Task 2 essay archetypes**.
  - **Screenshot Upload (`Ctrl/Cmd+V`)**: Paste or drop chart screenshots from external prep books (e.g., Cambridge IELTS); they are compressed to WebP in the browser and stored privately in Azure Blob Storage.
  - **AI Prompt & Chart Generator**: Generates fresh structured visual datasets and discursive prompts on demand.
- **Post-Submission Diagnostic & Multi-Draft (`v1 → v2 → v3`) Loop**
  - Official 4-criterion scoring (`TA/TR`, `CC`, `LR`, `GRA` in `0.5` steps), interactive color-coded inline span annotations, toggleable *"Your Ideas at Band 8.0+"* side-by-side diff, and iterative `v2/v3` re-scoring with band delta tracking.
- **Between-Essay Retention**
  - Auto-populated **Personal Error & Academic Collocation Vault** (with spaced-recall Flashcard Deck) + **5-Minute Targeted Micro-Drills** (`4m Overview Sprint`, `Cohesion De-Mechanizer`, `L1 Surgeon`, `Lexical & Hedging Upgrade`).
- **Multi-Provider AI Routing (OpenRouter Unified Default)**
  - Routes requests through OpenRouter's unified API using **Claude Sonnet 5.5** for high-precision 4-criterion IELTS rubric scoring, and **DeepSeek V4.1 Flash** for multimodal chart screenshot extraction, outlines, and 5-minute micro-drills (~$1.70/month total under heavy practice). Automatic failover fallback arrays, Zero Data Collection policy, and real-time USD spend tracking. Direct **DeepSeek**, **Google Gemini**, **OpenAI**, or **Anthropic Claude** keys remain fully supported in Settings (`AES-256-GCM` encrypted) or environment variables.

---

## How it is built

| Part | Where | What |
|---|---|---|
| Browser app | [`client/`](client/) | React 19, React Router, Tailwind 4, built with Vite |
| API | [`server/src/Escribot.Api`](server/src/Escribot.Api) | ASP.NET Core 11 minimal API; serves the built browser app from `wwwroot` |
| Local orchestration | [`server/src/Escribot.AppHost`](server/src/Escribot.AppHost) | Aspire AppHost: the API, the Cosmos DB emulator, Azurite and the Vite dev server |
| Data | Azure Cosmos DB (serverless) and Blob Storage | Described in [server/docs/data-model.md](server/docs/data-model.md) |
| Email | Azure Communication Services | Verification and password-reset links |
| Infrastructure | [`infra/`](infra/main.bicep), [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml) | Bicep for Azure Container Apps, Key Vault and the rest |

More detail: [architecture overview](docs/architecture.md), [sign-in](server/docs/sign-in.md), [data API](server/docs/data-api.md), [AI](server/docs/ai.md), [billing and admin](server/docs/billing.md).

### Accounts & data isolation

- Accounts use email + password, with optional "Continue with Google". Sessions are opaque random tokens; the database keeps only their SHA-256.
- Every essay, draft, evaluation, Vault item, drill, custom prompt, setting and AI usage record belongs to one user and lives in that user's Cosmos DB partition (`/userId`). Curated prompts are the only shared documents.
- Repository calls always take the user id, so one account can never read or change another's documents, even knowing their ids. `TenantIsolationTests` and `scripts/test-isolation.mjs` check this.

### Plans, quotas & rate limits

- **Platform keys are metered.** Requests paid for by the deployment's keys count toward the account's plan (`server/src/Escribot.Api/Common/Plans.cs`): by default Free = 3 graded essays + 30 coaching requests per month, Pro = 100 + 1,500. Set `FREE_PLAN_EVALUATIONS_PER_MONTH`, `FREE_PLAN_ASSISTS_PER_MONTH`, `PRO_PLAN_EVALUATIONS_PER_MONTH` or `PRO_PLAN_ASSISTS_PER_MONTH` to change them; each must be a whole number from 0 to 1,000,000, and the app refuses to start on any other value rather than fall back. They are read only from server configuration, never from a request, and the landing, pricing and Settings pages show the values in force. Limits reset on the 1st (UTC). The `OWNER_EMAIL` account is unlimited.
- **Own key = no limits.** A user who saves their own provider key in Settings pays for their own usage and is never metered.
- **Model choice is plan-gated.** On platform keys, Free accounts always use the default provider and models; Pro and own-key users can choose.
- **Free AI needs a real address.** In production, Free accounts can use the platform keys only once their email is verified (or they signed in with Google, or are listed in `ALLOWED_SIGNUP_EMAILS`), so throwaway sign-ups cannot multiply the allowance. Set `FREE_PLAN_DAILY_AI_CAP` (e.g. `500`) to also cap total Free usage per day across all accounts.
- **Rate limits** are counted in Cosmos DB so they hold across replicas: 20 AI requests per minute per user, plus per-address limits on sign-in, sign-up and password reset.
- To give someone complimentary Pro without a subscription, use the toggle on `/admin`.

### Billing (Stripe)

Paid Pro runs on Stripe Checkout and the Stripe customer portal; the app never handles card details.

1. In Stripe, create a **Pro** product with a recurring price. Suggested: **$9/month** and **$90/year** (two months free).
2. In **Settings → Billing → Customer portal**, enable cancellations, payment-method updates, invoice history and (if you created both prices) switching between them.
3. Add a webhook endpoint at `<App:PublicUrl>/api/stripe/webhook` for `customer.subscription.created`, `.updated`, `.deleted`, `.paused` and `.resumed`.
4. Set `STRIPE_SECRET_KEY`, `STRIPE_PRICE_PRO_MONTHLY`, optionally `STRIPE_PRICE_PRO_YEARLY`, and `STRIPE_WEBHOOK_SECRET`.

How it works:

- `/pricing` is public. Free users see an **Upgrade** button in the header, in Settings, and in the message shown when they run out of their monthly allowance.
- Checkout creates one Stripe customer per user. The webhook stores the subscription; `active`, `trialing` and `past_due` grant Pro (past-due keeps access while Stripe retries the card).
- **Manage billing** in Settings opens the Stripe portal for cancelling, changing card or billing period, and invoices. A cancelled subscription keeps Pro until the end of the paid period.
- Test locally with the Stripe CLI: `stripe listen --forward-to <API address>/api/stripe/webhook` and use its `whsec_...` secret.

### Onboarding, landing page and legal pages

- Signed-out visitors to `/` see the landing page; signed-in users get the studio. `/pricing`, `/terms` and `/privacy` are public.
- New accounts answer a short calibration step at `/welcome` (current and target band, exam date, first language) before reaching the studio.
- `/terms` and `/privacy` are written to match what the app actually does with data (providers, retention, deletion). Set `SITE_OPERATOR`, `SUPPORT_EMAIL` and `SITE_JURISDICTION`, and **have them reviewed by a lawyer before launch**; they are a starting point, not legal advice.
- The footer carries a trademark notice: Escribot is not affiliated with the IELTS partners, and band scores are AI estimates.

### Account deletion and data export

**Settings → Account & Privacy** lets a user:

- Download everything held about them as one JSON file (`/api/account/export`). Saved API keys are listed but never exported.
- Delete their account, after confirming with their password. This deletes the Stripe customer (cancelling any subscription immediately), uploaded images and every document they own.

### Chart image storage

Uploaded Task 1 screenshots are stored in a private Blob Storage container and served only to their owner through `/api/images/{id}`. Limits: 2 MB per image; 25 MB total on Free and 250 MB on Pro.

### Admin and error tracking

- `/admin` is visible to the verified `OWNER_EMAIL` account and verified addresses in `ADMIN_EMAILS`; everyone else gets a 404. It shows users, sign-ups, paying subscribers, platform AI cost by plan, recent errors and a complimentary-Pro toggle per user.
- Server errors and browser error reports (`/api/errors`) are stored in Cosmos DB for 30 days, with anything shaped like a credential redacted. Traces, metrics and logs go to OpenTelemetry (the Aspire dashboard locally and in Azure).

---

## Local development

**You need:** the [.NET 11 SDK](https://dotnet.microsoft.com/download), Node.js 22 or later, and a running container runtime (Docker Desktop or Podman). The Cosmos DB emulator and Azurite run as containers.

```bash
cd server
dotnet run --project src/Escribot.AppHost
```

This starts the API, the Linux-based Cosmos DB emulator, Azurite and the Vite dev server (`web`, after an `npm install` in `client/`), creates the database, its containers and the blob container in the emulators, and prints a link to the Aspire dashboard, where you can see each resource with its logs, traces and metrics. Open the app at the `web` resource's address and sign up. The first run downloads the emulator images. Without email configured, verification and password-reset links are printed to the API's console.

The browser app on its own:

```bash
cd client
npm install
npm run dev        # expects the API at http://localhost:5271, or set API_HTTP
npm run lint && npm run typecheck && npm run build
```

| Project | Purpose |
|---|---|
| `src/Escribot.Api` | ASP.NET Core minimal API; also serves the browser app from `wwwroot` |
| `../client` | The browser app: Vite, React, React Router and Tailwind 4 |
| `src/Escribot.AppHost` | Aspire AppHost: starts the API and the emulators for local development |
| `src/Shared` | The database and container definitions, compiled into both the AppHost and the API |
| `src/Escribot.ServiceDefaults` | Shared OpenTelemetry, health checks, service discovery and HTTP resilience |
| `tests/Escribot.Api.Tests` | xUnit tests |

### Settings and secrets

No secret has a default value in the code, and none belongs in a file inside the repository. The API refuses to start if a required setting is missing.

| Setting | Environment variable | Required |
|---|---|---|
| `App:EncryptionSecret` | `App__EncryptionSecret` | Always. At least 32 characters. Protects the API keys users save. |
| `App:PublicUrl` | `App__PublicUrl` | In production, where it must use HTTPS. |
| `ConnectionStrings:cosmos` | `ConnectionStrings__cosmos` | Always. Supplied by the AppHost locally. |
| `ConnectionStrings:blobs` | `ConnectionStrings__blobs` | Always. Supplied by the AppHost locally. |
| `App:TrustForwardedHeaders` | `App__TrustForwardedHeaders` | Set to `true` behind the Azure Container Apps ingress, and only behind a proxy. |
| `App:CloudflareIpRanges` | `App__CloudflareIpRanges` | Behind Cloudflare's proxy only: Cloudflare's CIDR ranges, comma-separated. The deployment sets it. |
|  | `OWNER_EMAIL`, `ALLOWED_SIGNUP_EMAILS`, `ADMIN_EMAILS` | Optional. The owner account, a sign-up allowlist and extra admins (see above). |
|  | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Optional. Both turn on "Continue with Google". |
|  | `AZURE_COMMUNICATION_CONNECTION_STRING`, `EMAIL_FROM` | Optional. Both turn on email through Azure Communication Services. |
|  | `SITE_OPERATOR`, `SUPPORT_EMAIL`, `SITE_JURISDICTION` | Optional. Shown on the Terms and Privacy pages (public, from `GET /api/config`). |

When you run the AppHost, it generates a random `App:EncryptionSecret` the first time and saves it in [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), which are stored in your user profile, outside the repository. To set your own value instead:

```bash
cd server
dotnet user-secrets set "Parameters:encryption-secret" "$(openssl rand -hex 32)" --project src/Escribot.AppHost
```

To run the API on its own, without the AppHost, give it the same settings through user secrets or environment variables.

#### Secrets in Azure (Key Vault)

Key Vault has no local emulator, so it is only part of the published app: local runs keep passing secrets straight from user secrets. When the AppHost is published to Azure (for example with `aspire deploy` or `azd up`), it adds:

- an Azure Key Vault (`vault`) that uses Azure RBAC, holding `encryption-secret` and, when they are set, `stripe-secret-key` and `stripe-webhook-secret`;
- an Azure Container Apps environment (`aca`) for the API, whose managed identity gets only the **Key Vault Secrets User** role on the vault.

The API still receives `App__EncryptionSecret`, `STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET` as environment variables. In Azure, Container Apps reads them from the vault, so the values never appear in the generated Bicep or the deployment manifest, and the API code does not change. The deployment asks for `encryption-secret`; the Stripe secrets are included only when `STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET` are set in the AppHost's configuration when you publish. To see what would be deployed without deploying it:

```bash
cd server
dotnet run --project src/Escribot.AppHost -- --operation publish --publisher manifest --output-path "$(mktemp -d)"
```

To store another secret in the vault, add a secret parameter in `AppHost.cs` and pass it to `WithSecret`.

### Tests

```bash
cd server
dotnet test                                      # everything; needs a container runtime
dotnet test --filter "Category!=Integration"     # skips the tests that start the emulators
```

`scripts/test-isolation.mjs` signs up two throwaway accounts and checks that one cannot read or change the other's data on any route, that signed-out requests get 401, and that deleting an account leaves the other intact. It runs against any address (`BASE_URL=... node scripts/test-isolation.mjs`). CI runs it against the Docker image, started with the emulators in place of the API project:

```bash
docker build -f server/Dockerfile -t escribot-api:ci .
cd server
ISOLATION_TEST_IMAGE=escribot-api:ci dotnet test --filter "Category=Image"
```

### Docker image

Build from the repository root, so the image can include the browser app:

```bash
docker build -f server/Dockerfile -t escribot-api .
```

The image contains the API and the built browser app, which the API serves with `index.html` for every page address: no secrets and no development settings. It listens on port 8080 and answers `/alive` (the process is up) and `/health` (Cosmos DB and Blob Storage are reachable).

## Deploying to Azure

The `.NET 11` app deploys to Azure Container Apps from the infrastructure definitions in [`infra/`](infra/main.bicep) (derived from the Aspire AppHost) via [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml). It is designed to cost **$0 when idle** and to keep every credential and Azure identifier out of this public repository:

- **No Azure Container Registry**: the Docker image is built in GitHub Actions and pushed to GitHub Container Registry (`ghcr.io`), so there is no ~$5/month registry fee.
- **Scale to zero**: the Container App runs on the Consumption plan with `minReplicas: 0` and `maxReplicas: 1` (so a flood of requests can't multiply the compute bill), startup/liveness/readiness probes on `/alive` and `/health`, and `App__TrustForwardedHeaders=true`.
- **Serverless & private storage**: Cosmos DB runs in Serverless mode (`disableLocalAuth: true`), and Blob Storage uses `Standard_LRS` with `allowSharedKeyAccess: false` and `allowBlobPublicAccess: false` (`files` container has `publicAccess: None`). The Container App accesses both via its user-assigned managed identity.
- **Key Vault for secrets**: application secrets are stored in an RBAC-enabled Azure Key Vault and referenced from Container Apps via managed identity (`Key Vault Secrets User`).
- **Telemetry**: the Aspire Dashboard component on Container Apps provides traces, metrics, and logs (protected by Microsoft Entra ID / Azure RBAC — never reachable anonymously), and Log Analytics uses 30-day retention and a 0.15 GB daily ingestion cap, keeping it inside the 5 GB monthly free allowance (logs past the cap are dropped until the next UTC day).
- **Cloudflare in front (optional, free)**: with `BEHIND_CLOUDFLARE=true`, the custom domain is served through Cloudflare's proxy, which absorbs floods and bot traffic before they wake the Container App or write rate-limit counters to Cosmos DB. The ingress then admits only Cloudflare's addresses, so the `azurecontainerapps.io` address can't be used to skip it, and the API keys its rate limits on the visitor's address from `CF-Connecting-IP`. See [Cloudflare proxy](#cloudflare-proxy).
- **Budget and kill switch**: a monthly budget (`Microsoft.Consumption/budgets`, amount from the `BUDGET_AMOUNT` secret, so it never appears in the repository or the Actions logs) counts only this app's resource group. When actual spend reaches 100%, an action group runs the `budget-killswitch` Logic App, which stops the Container App through the ARM API (its managed identity has Contributor on that one app). When the `BUDGET_ALERT_EMAIL` secret is set, the budget also emails at 50% actual, 100% actual, and 100% forecasted spend. Azure cost data lags by several hours, so spend can pass the budget before the app stops. While the app is stopped, the deploy workflow refuses to run; bring it back with `az containerapp start -g <resource group> -n api` (for example, after raising `BUDGET_AMOUNT` or at the start of the next month). Set the Bicep parameter `enableBudgetKillSwitch` to `false` to keep only the email alerts.

### One-time setup (owner)

1. **Azure OIDC app registration**: create a Microsoft Entra ID app registration with a federated credential scoped to this repository's `main` branch (or the `production` GitHub environment), and grant its service principal `Owner` (or `Contributor` + `User Access Administrator`) on the subscription so it can create the resource group, RBAC role assignments, and subscription budget.
2. **GitHub `production` environment**: in **Settings → Environments**, create an environment named `production` with **Required reviewers** enabled. Deployment only runs on `main` and never on pull requests or forks.
3. **GitHub environment secrets** (`production` environment):
   - `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` — OIDC credentials for `azure/login`.
   - `APP_ENCRYPTION_SECRET` — required; at least 32 random characters (`openssl rand -hex 32`).
   - `BUDGET_AMOUNT` — required; the monthly budget as a whole number. A secret rather than a variable, so the amount stays out of this public repository and its Actions logs.
   - Optional secrets: `BUDGET_ALERT_EMAIL` (where budget alerts go; a secret so the address stays out of the public Actions logs), `ORIGIN_CERT_PFX` and `ORIGIN_CERT_PASSWORD` (the Cloudflare Origin certificate; see [Cloudflare proxy](#cloudflare-proxy)), `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` (to serve the browser app from Cloudflare; see the same section), `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `OPENROUTER_API_KEY`, `DEEPSEEK_API_KEY`, `GEMINI_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`.
4. **GitHub environment variables** (`production` environment):
   - `AZURE_RESOURCE_GROUP` — target resource group name.
   - `AZURE_LOCATION` — Azure region (e.g. `eastus2`).
   - Optional variables: `CUSTOM_DOMAIN`, `ENABLE_CUSTOM_DOMAIN_CERT` (`false` until DNS is added), `BEHIND_CLOUDFLARE` (`false` until the domain is proxied; see [Cloudflare proxy](#cloudflare-proxy)), `EMAIL_DOMAIN` (defaults to `CUSTOM_DOMAIN`), `APP_PUBLIC_URL`, `ACS_DATA_LOCATION` (defaults to `United States`), `STRIPE_PRICE_PRO_MONTHLY`, `STRIPE_PRICE_PRO_YEARLY`, `OWNER_EMAIL`, `ALLOWED_SIGNUP_EMAILS`, `ADMIN_EMAILS`, `EMAIL_FROM`, `FREE_PLAN_DAILY_AI_CAP`, `FREE_PLAN_EVALUATIONS_PER_MONTH`, `FREE_PLAN_ASSISTS_PER_MONTH`, `PRO_PLAN_EVALUATIONS_PER_MONTH`, `PRO_PLAN_ASSISTS_PER_MONTH`, `SITE_OPERATOR`, `SUPPORT_EMAIL`, `SITE_JURISDICTION`.
5. **Public GHCR package**: after the first `build` job pushes `ghcr.io/<owner>/<repo>`, ensure the package visibility is **Public** in GitHub Packages so Container Apps can pull without credentials.
6. **Custom domain & free managed certificate**:
   - Run the first deploy with `ENABLE_CUSTOM_DOMAIN_CERT=false` to provision the environment and read `apiFqdn` and `customDomainVerificationId` from the deployment outputs.
   - Add a `CNAME` record for your domain pointing to `apiFqdn`, and a `TXT` record `asuid.<domain>` with `customDomainVerificationId`.
   - Set `ENABLE_CUSTOM_DOMAIN_CERT=true` in the `production` environment variables and re-run the deploy workflow.
7. **Email domain verification (ACS)**: when `CUSTOM_DOMAIN` (or `EMAIL_DOMAIN`) is set, Azure Communication Services provisions the email domain. In the Azure Portal under the Email Communication Service domain, copy the **domain verification TXT**, **SPF** and **DKIM** DNS records to your DNS provider and verify them. Azure only links a verified domain, so the deploy fails at the `acs` step until verification passes; verify, then re-run it.
   - The verification and SPF records are TXT records on the email domain itself, and a name with a `CNAME` can't hold other records. When `CUSTOM_DOMAIN` is a `CNAME` (any subdomain pointed at the Container App), set `EMAIL_DOMAIN` to a subdomain such as `mail.<CUSTOM_DOMAIN>`; email then goes out from `DoNotReply@<EMAIL_DOMAIN>`.
8. **External callbacks**:
   - Stripe webhook endpoint: `https://<your-domain>/api/stripe/webhook`
   - Google OAuth redirect URI: `https://<your-domain>/api/auth/callback/google`
9. **Verify isolation**:
   ```bash
   BASE_URL=https://<your-domain> node scripts/test-isolation.mjs
   ```

### Cloudflare proxy

Cloudflare's free plan in front of the custom domain stops floods at the edge, for $0 (Azure Front Door would cost about $35 a month). Set it up after the custom domain works without it (step 6 above).

1. **Get an Origin certificate.** Azure's free managed certificate can't be issued or renewed once the domain is proxied: Container Apps needs the `CNAME` to point straight at the app and DigiCert to reach it, and neither holds behind Cloudflare with the ingress restricted to Cloudflare's addresses. In the Cloudflare dashboard, go to **SSL/TLS → Origin Server → Create Certificate**, list the custom domain, and keep the default 15-year validity. Save the certificate and key, then make a PFX of them:
   ```bash
   openssl pkcs12 -export -in origin.pem -inkey origin.key -out origin.pfx -passout pass:<password>
   base64 < origin.pfx | tr -d '\n'   # the value of ORIGIN_CERT_PFX
   ```
   Add `ORIGIN_CERT_PFX` and `ORIGIN_CERT_PASSWORD` to the `production` environment secrets and delete the local files. Cloudflare's browser-facing certificate is separate and managed by Cloudflare; the Origin certificate only secures Cloudflare → Azure, and only Cloudflare trusts it.
2. **Move DNS to Cloudflare.** Add the domain to Cloudflare (or move its nameservers there). Recreate the app's `CNAME` (to `apiFqdn`), the `asuid.<domain>` `TXT` record, and the email records from step 7, all as **DNS only** for now.
3. **Turn on the proxy.** Switch the app's `CNAME` to **Proxied** (orange cloud). Leave `asuid` and the email records as DNS only. In **SSL/TLS → Overview**, set the encryption mode to **Full (strict)**. The managed certificate is still bound and publicly trusted, so Cloudflare accepts it. Check that `https://<your-domain>/health` answers with a `cf-ray` header. Do this before binding the Origin certificate: only Cloudflare trusts that certificate, so browsers reaching the app directly, while the record is DNS only, would reject it.
4. **Bind the Origin certificate and restrict the ingress.** Set `BEHIND_CLOUDFLARE=true` in the `production` environment variables and re-run the deploy workflow. With `ORIGIN_CERT_PFX` set, the deployment uploads it to the Container Apps environment and binds it to the custom domain in place of the managed certificate. The Container App then accepts requests only from Cloudflare's IPv4 ranges (others get `403`), and the API reads the visitor's address from `CF-Connecting-IP` for requests that came from those ranges. The workflow's health check calls the custom domain through Cloudflare, and fails the run if the `azurecontainerapps.io` address still answers directly.
5. **Turn on Cloudflare's free protections:**
   - **Security → Bots → Bot Fight Mode**: on. Afterwards, check that Stripe webhook deliveries still succeed in the Stripe dashboard.
   - **Security → WAF → Rate limiting rules**: the free plan allows one rule. For example, when the URI path starts with `/api/auth/`, block an IP that sends more than 20 requests in 10 seconds, for 10 seconds.
   - **Under Attack mode** (**Overview → Quick Actions**): leave off, and switch it on during an incident. It shows every visitor a JavaScript challenge, so API calls from a page loaded before the switch may fail until it is reloaded.
6. **Serve the browser app from Cloudflare (optional, free).** The API scales to zero, and the first request after about five idle minutes waits roughly 30 seconds for it to start. With this step the page itself comes from Cloudflare's edge at once, and the app shows "Starting up…" until the API answers. In the Cloudflare dashboard, create an API token (**My Profile → API Tokens → Create Token → Custom token**) with **Account › Workers Scripts › Edit**, **Zone › Workers Routes › Edit** and **Zone › Zone › Read**, limited to your account and the domain's zone. Add it to the `production` environment secrets as `CLOUDFLARE_API_TOKEN`, add your account ID (on the zone's **Overview** page) as `CLOUDFLARE_ACCOUNT_ID`, and re-run the deploy workflow. Its `web` job builds `client/`, adds `<domain>/api/*`, `<domain>/alive` and `<domain>/health` as routes with no Worker, so those still go straight to Azure, and then deploys the Worker in [`client/wrangler.jsonc`](client/wrangler.jsonc) on `<domain>/*`. The Worker has no script, only static assets, which the Workers free plan serves without a request limit. Its response headers are in [`client/cloudflare/_headers`](client/cloudflare/_headers), and a test checks they match the API's. To turn it off, delete the `<domain>/*` route under **Workers Routes**: the API serves the app again, as it always can.

**Updating Cloudflare's IP ranges.** The ranges live in one file, [`infra/cloudflare-ips.json`](infra/cloudflare-ips.json), which feeds both the ingress rules (IPv4 entries only; the Container Apps ingress is IPv4) and `App__CloudflareIpRanges`. Cloudflare publishes the current list at <https://www.cloudflare.com/ips/> (plain text at `/ips-v4` and `/ips-v6`) and changes it rarely. To update, replace the file's contents and deploy:
```bash
{ curl -s https://www.cloudflare.com/ips-v4; echo; curl -s https://www.cloudflare.com/ips-v6; } \
  | jq -R 'select(length > 0)' | jq -s . > infra/cloudflare-ips.json
```
If a new range is missing, visitors routed through it get `403` from the ingress.

**Testing locally.** [`scripts/test-cloudflare-ip.sh`](scripts/test-cloudflare-ip.sh) sends sign-in requests shaped like the ones the ingress forwards from Cloudflare to a locally running API, and checks that visitors sharing a Cloudflare edge address are rate-limited separately and that a `CF-Connecting-IP` header from anyone else is ignored. Its header says how to start the API with the Cloudflare settings.

**Turning it off.** Set the `CNAME` back to DNS only, set `BEHIND_CLOUDFLARE=false`, and re-run the deploy workflow. To go back to the free managed certificate, also delete `ORIGIN_CERT_PFX` and make sure `ENABLE_CUSTOM_DOMAIN_CERT=true`.

### Full environment variable reference

| Variable | Secret | Required | Purpose |
|---|---|---|---|
| `App__EncryptionSecret` | Yes | Yes | 32+ char secret used to encrypt user-saved AI API keys. |
| `App__PublicUrl` | No | In prod | Public HTTPS origin used for email links, OAuth, and Stripe redirects. |
| `App__TrustForwardedHeaders` | No | Behind proxy | Set to `true` behind Container Apps ingress for `X-Forwarded-For` / `X-Forwarded-Proto`. |
| `App__CloudflareIpRanges` | No | Behind Cloudflare | Cloudflare's CIDR ranges, comma-separated; set from `infra/cloudflare-ips.json` when `BEHIND_CLOUDFLARE=true`. Requests from those ranges take the client address from `CF-Connecting-IP`. |
| `ConnectionStrings__cosmos` | No | Yes | Cosmos DB account endpoint (uses Managed Identity in Azure). |
| `ConnectionStrings__blobs` | No | Yes | Blob Storage endpoint (uses Managed Identity in Azure). |
| `AZURE_CLIENT_ID` | No | In Azure | User-assigned managed identity client ID. |
| `AZURE_TOKEN_CREDENTIALS` | No | In Azure | Set to `ManagedIdentityCredential` in production. |
| `OWNER_EMAIL` | No | No | Verified account with this email is the site owner/admin. |
| `ALLOWED_SIGNUP_EMAILS` | No | No | Comma-separated allowlist of emails permitted to sign up. |
| `ADMIN_EMAILS` | No | No | Comma-separated list of additional admin emails. |
| `GOOGLE_CLIENT_ID` | No | No | Google OAuth client ID (enables "Continue with Google" with `GOOGLE_CLIENT_SECRET`). |
| `GOOGLE_CLIENT_SECRET` | Yes | No | Google OAuth client secret. |
| `AZURE_COMMUNICATION_CONNECTION_STRING` | Yes | No | ACS Email connection string (provisioned to Key Vault when `CUSTOM_DOMAIN` or `EMAIL_DOMAIN` is set). |
| `EMAIL_FROM` | No | No | Bare sender address (defaults to `DoNotReply@<EMAIL_DOMAIN>`, or `DoNotReply@<CUSTOM_DOMAIN>`). |
| `STRIPE_SECRET_KEY` | Yes | No | Stripe secret API key. |
| `STRIPE_WEBHOOK_SECRET` | Yes | No | Stripe webhook signing secret. |
| `STRIPE_PRICE_PRO_MONTHLY` | No | No | Stripe monthly Pro price ID. |
| `STRIPE_PRICE_PRO_YEARLY` | No | No | Stripe yearly Pro price ID. |
| `OPENROUTER_API_KEY` | Yes | No | Platform OpenRouter API key for Free-tier users. |
| `DEEPSEEK_API_KEY` | Yes | No | Platform DeepSeek API key. |
| `GEMINI_API_KEY` | Yes | No | Platform Gemini API key. |
| `OPENAI_API_KEY` | Yes | No | Platform OpenAI API key. |
| `ANTHROPIC_API_KEY` | Yes | No | Platform Anthropic API key. |
| `FREE_PLAN_DAILY_AI_CAP` | No | No | Daily ceiling on Free-plan AI requests across all accounts. |
| `FREE_PLAN_EVALUATIONS_PER_MONTH` | No | No | Free graded essays per month (default 3). |
| `FREE_PLAN_ASSISTS_PER_MONTH` | No | No | Free coaching requests per month (default 30). |
| `PRO_PLAN_EVALUATIONS_PER_MONTH` | No | No | Pro graded essays per month (default 100). |
| `PRO_PLAN_ASSISTS_PER_MONTH` | No | No | Pro coaching requests per month (default 1,500). |
| `SITE_OPERATOR` | No | No | Operator name shown on Terms and Privacy pages. |
| `SUPPORT_EMAIL` | No | No | Support email shown on Terms and Privacy pages. |
| `SITE_JURISDICTION` | No | No | Governing jurisdiction shown on Terms and Privacy pages. |
