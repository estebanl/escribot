# FluentEscritor

**FluentEscritor** is an Academic IELTS Writing Coach & Authentic Computer-Delivered (Inspera) Exam Simulator built to help writers break past the **Band 6.5 → 7.5/8.0+ plateau**, with private per-user accounts, deployed on Vercel + Turso.

## Core Capabilities

- **Dual Writing Modes**
  - **Editorial Coach Studio**: 5-minute interactive Outline Check (validating Task 1 number-free macro Overviews & Task 2 Thesis/paragraph structure before writing), distraction-free serif/sans editor, and surgical on-demand tools (**Academic Collocations & Hedging**, **Spanish → English L1 Interference Scan**, **Single-Paragraph Check**) with strictly zero ghost-text autocomplete.
  - **Authentic Inspera Exam Simulator**: Faithful Computer-Delivered IELTS replica supporting **20m Task 1**, **40m Task 2**, and **60m Shared-Clock Full Mock Exams** (`Part 1` / `Part 2` switcher tabs, `1:2` weighted overall Writing Band `(T1 + 2 × T2) / 3`, prompt yellow highlighter, candidate scratchpad notes, spellcheck disabled).
- **Full Academic Task 1 & Task 2 Coverage**
  - Pre-seeded library spanning **all 7 Task 1 visual archetypes** (Line Graph, Bar Chart, Comparative Pie Charts, Data Table, Mixed Dual-Axis Charts, Process Diagram, Two-Period Comparison Map) and **all 5 Task 2 essay archetypes**.
  - **Screenshot Upload (`Ctrl/Cmd+V`)**: Paste or drop chart screenshots from external prep books (e.g., Cambridge IELTS); client-side WebP compression stores images directly in Turso without external S3 buckets.
  - **AI Prompt & Chart Generator**: Generates fresh structured visual datasets and discursive prompts on demand.
- **Post-Submission Diagnostic & Multi-Draft (`v1 → v2 → v3`) Loop**
  - Official 4-criterion scoring (`TA/TR`, `CC`, `LR`, `GRA` in `0.5` steps), interactive color-coded inline span annotations, toggleable *"Your Ideas at Band 8.0+"* side-by-side diff, and iterative `v2/v3` re-scoring with band delta tracking.
- **Between-Essay Retention**
  - Auto-populated **Personal Error & Academic Collocation Vault** (with spaced-recall Flashcard Deck) + **5-Minute Targeted Micro-Drills** (`4m Overview Sprint`, `Cohesion De-Mechanizer`, `L1 Surgeon`, `Lexical & Hedging Upgrade`).
- **Multi-Provider AI Routing (OpenRouter Unified Default)**
  - Routes requests through OpenRouter's unified API using **Claude Sonnet 5.5** for high-precision 4-criterion IELTS rubric scoring, and **DeepSeek V4.1 Flash** for multimodal chart screenshot extraction, outlines, and 5-minute micro-drills (~$1.70/month total under heavy practice). Automatic failover fallback arrays, Zero Data Collection policy, and real-time USD spend tracking. Direct **DeepSeek**, **Google Gemini**, **OpenAI**, or **Anthropic Claude** keys remain fully supported in Settings (`AES-256-GCM` encrypted) or environment variables.

---

## Local Development

```bash
npm install
cp .env.example .env.local
npm run dev
```

On first boot `FluentEscritor` creates `file:./local.db` (SQLite/LibSQL), applies the SQL migrations in `/drizzle`, and seeds the curated prompt bank — no manual migration command required. Open `http://localhost:3000/signup` to create your account.

Without email configured, password-reset links are printed to the dev server console.

### Accounts & data isolation

- Sign-in is handled by [Better Auth](https://www.better-auth.com) (email + password, optional Google), with sessions stored in the same database.
- Every essay, draft, evaluation, Vault item, drill, custom prompt, settings row, and AI usage record belongs to one user (`user_id`). Curated prompts are the only shared rows.
- Server pages call `requireUser()` and API routes call `getCurrentUser()` (`src/lib/session.ts`); ownership filters live in `src/lib/db/scope.ts`.

### Plans, quotas & rate limits

- **Platform keys are metered.** Requests paid for by the deployment's env keys count toward the account's plan (`src/lib/plans.ts`): Free = 5 graded essays + 60 coaching requests per month, Pro = 100 + 1,500. Limits reset on the 1st (UTC). The `OWNER_EMAIL` account is unlimited.
- **Own key = no limits.** A user who saves their own provider key in Settings pays for their own usage and is never metered.
- **Model choice is plan-gated.** On platform keys, Free accounts always use the default provider and models; Pro and own-key users can choose.
- **Free AI needs a real address.** In production, Free accounts can use the platform keys only once their email is verified (or they signed in with Google, or are listed in `ALLOWED_SIGNUP_EMAILS`), so throwaway sign-ups cannot multiply the allowance. Set `FREE_PLAN_DAILY_AI_CAP` (e.g. `500`) to also cap total Free usage per day across all accounts.
- **Rate limits** are stored in the database so they hold across serverless instances: 20 AI requests per minute per user, plus per-IP limits on sign-in, sign-up and password reset.
- To give someone complimentary Pro without a subscription: `UPDATE user_settings SET plan = 'pro' WHERE user_id = '<id>';`

### Billing (Stripe)

Paid Pro runs on Stripe Checkout and the Stripe customer portal; the app never handles card details.

1. In Stripe, create a **Pro** product with a recurring price. Suggested: **$9/month** and **$90/year** (two months free).
2. In **Settings → Billing → Customer portal**, enable cancellations, payment-method updates, invoice history and (if you created both prices) switching between them.
3. Add a webhook endpoint at `<BETTER_AUTH_URL>/api/stripe/webhook` for `customer.subscription.created`, `.updated`, `.deleted`, `.paused` and `.resumed`.
4. Set `STRIPE_SECRET_KEY`, `STRIPE_PRICE_PRO_MONTHLY`, optionally `STRIPE_PRICE_PRO_YEARLY`, and `STRIPE_WEBHOOK_SECRET`.

How it works:

- `/pricing` is public. Free users see an **Upgrade** button in the header, in Settings, and in the message shown when they run out of their monthly allowance.
- Checkout creates one Stripe customer per user. The webhook stores the subscription in the `subscriptions` table; `active`, `trialing` and `past_due` grant Pro (past-due keeps access while Stripe retries the card).
- **Manage billing** in Settings opens the Stripe portal for cancelling, changing card or billing period, and invoices. A cancelled subscription keeps Pro until the end of the paid period.
- Test locally with the Stripe CLI: `stripe listen --forward-to localhost:3000/api/stripe/webhook` and use its `whsec_...` secret.

### Onboarding, landing page and legal pages

- Signed-out visitors to `/` see the landing page; signed-in users get the studio. `/pricing`, `/terms` and `/privacy` are public.
- New accounts answer a short calibration step at `/welcome` (current and target band, exam date, first language) before reaching the studio.
- `/terms` and `/privacy` are written to match what the app actually does with data (providers, retention, deletion). Set `SITE_OPERATOR`, `SUPPORT_EMAIL` and `SITE_JURISDICTION`, and **have them reviewed by a lawyer before launch**; they are a starting point, not legal advice.
- The footer carries a trademark notice: FluentEscritor is not affiliated with the IELTS partners, and band scores are AI estimates.

### Account deletion and data export

**Settings → Account & Privacy** lets a user:

- Download everything held about them as one JSON file (`/api/account/export`). Saved API keys are listed but never exported.
- Delete their account, after confirming with their password. This deletes the Stripe customer (cancelling any subscription immediately), uploaded images and every row they own.

### Chart image storage

Uploaded Task 1 screenshots are stored in a private Vercel Blob store when `BLOB_READ_WRITE_TOKEN` is set, or in an `images` table otherwise. They are served only to their owner through `/api/images/[id]`.

Limits: 2 MB per image; 25 MB total on Free and 250 MB on Pro (`src/lib/plans.ts`). Images uploaded before this change stay inline until an admin moves them from `/admin`.

### Admin and error tracking

- `/admin` is visible to the verified `OWNER_EMAIL` account and verified addresses in `ADMIN_EMAILS`; everyone else gets a 404. It shows users, sign-ups, paying subscribers, platform AI cost by plan, recent errors, a complimentary-Pro toggle per user, and the inline-image migration.
- Errors are recorded in the `error_events` table (kept 30 days) from three places: uncaught server errors (`src/instrumentation.ts`), API routes' own error handling, and browser error boundaries (`/api/errors`). To use Sentry or similar instead, forward from `reportError` in `src/lib/errors.ts`.

### Tests

`npm run test:isolation` runs against a live server (`BASE_URL`, default `http://localhost:3000`). It creates two throwaway accounts and checks that one cannot read or change the other's data on any route, that signed-out requests get 401, and that deleting an account leaves other users intact. CI (`.github/workflows/ci.yml`) runs lint, type-check, build and this test on every pull request.

### Database migrations

Schema changes are versioned: edit `src/lib/db/schema.ts`, run `npm run db:generate`, and commit the new file in `/drizzle`. Migrations are applied automatically at startup (or manually with `npm run db:migrate`).

---

## Local development (.NET)

The app is being moved to a .NET 11 server on Azure (see issue #7). The new server lives in `server/` next to the Next.js app, which keeps working until the move is finished. So far it has health endpoints, configuration, the data layer (the Cosmos DB containers and the repositories over them, described in [server/docs/data-model.md](server/docs/data-model.md)) and sign-in, described in [server/docs/sign-in.md](server/docs/sign-in.md). The rest of the application endpoints are not there yet.

**You need:** the [.NET 11 SDK](https://dotnet.microsoft.com/download) and a running container runtime (Docker Desktop or Podman). The Cosmos DB emulator and Azurite run as containers.

```bash
cd server
dotnet run --project src/FluentEscritor.AppHost
```

This starts the API, the Linux-based Cosmos DB emulator and Azurite, creates the database and its containers in the emulator, and prints a link to the Aspire dashboard, where you can see each resource with its logs, traces and metrics. The first run downloads the emulator images.

| Project | Purpose |
|---|---|
| `src/FluentEscritor.Api` | ASP.NET Core minimal API |
| `src/FluentEscritor.AppHost` | Aspire AppHost: starts the API and the emulators for local development |
| `src/Shared` | The database and container definitions, compiled into both the AppHost and the API |
| `src/FluentEscritor.ServiceDefaults` | Shared OpenTelemetry, health checks, service discovery and HTTP resilience |
| `tests/FluentEscritor.Api.Tests` | xUnit tests |

### Settings and secrets

No secret has a default value in the code, and none belongs in a file inside the repository. The API refuses to start if a required setting is missing.

| Setting | Environment variable | Required |
|---|---|---|
| `App:EncryptionSecret` | `App__EncryptionSecret` | Always. At least 32 characters. Protects the API keys users save. |
| `App:PublicUrl` | `App__PublicUrl` | In production, where it must use HTTPS. |
| `ConnectionStrings:cosmos` | `ConnectionStrings__cosmos` | Always. Supplied by the AppHost locally. |
| `ConnectionStrings:blobs` | `ConnectionStrings__blobs` | Always. Supplied by the AppHost locally. |
| `App:TrustForwardedHeaders` | `App__TrustForwardedHeaders` | Set to `true` behind the Azure Container Apps ingress, and only behind a proxy. |
|  | `OWNER_EMAIL`, `ALLOWED_SIGNUP_EMAILS`, `ADMIN_EMAILS` | Optional. Same meaning as in the Next.js app. |
|  | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Optional. Both turn on "Continue with Google". |
|  | `AZURE_COMMUNICATION_CONNECTION_STRING`, `EMAIL_FROM` | Optional. Both turn on email through Azure Communication Services. |

When you run the AppHost, it generates a random `App:EncryptionSecret` the first time and saves it in [.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), which are stored in your user profile, outside the repository. To set your own value instead:

```bash
cd server
dotnet user-secrets set "Parameters:encryption-secret" "$(openssl rand -hex 32)" --project src/FluentEscritor.AppHost
```

To run the API on its own, without the AppHost, give it the same settings through user secrets or environment variables.

### Tests

```bash
cd server
dotnet test                                      # everything; needs a container runtime
dotnet test --filter "Category!=Integration"     # skips the tests that start the emulators
```

### Docker image

```bash
cd server
docker build -t fluentescritor-api .
```

The image contains the API only: no secrets and no development settings. It listens on port 8080 and answers `/alive` (the process is up) and `/health` (Cosmos DB and Blob Storage are reachable).

## Deploying to Vercel

1. **Create a free Turso database**:
   ```bash
   turso db create fluentescritor
   turso db show fluentescritor --url
   turso db tokens create fluentescritor
   ```
2. **Import the repository into Vercel** and set these Environment Variables:
   - `BETTER_AUTH_SECRET` — 32+ character random secret for session cookies (`openssl rand -base64 32`).
   - `BETTER_AUTH_URL` — Public URL of the deployment, e.g. `https://your-app.vercel.app`.
   - `TURSO_DATABASE_URL` — `libsql://...` URL from Turso.
   - `TURSO_AUTH_TOKEN` — Auth token from Turso.
   - `OPENROUTER_API_KEY` — Platform OpenRouter key used for users who have not saved their own key in **Settings**.
   - `APP_SECRET` — Required. Long random secret for `AES-256-GCM` encryption of API keys saved in Settings.
   - *(Optional)* `ALLOWED_SIGNUP_EMAILS` — Comma-separated allowlist of emails that may sign up. Leave empty for open sign-ups; each Free account can then spend the platform AI keys up to its monthly quota.
   - *(Strongly recommended)* `RESEND_API_KEY` + `EMAIL_FROM` — Email verification and password-reset links. Without them, password reset is disabled and Free accounts can only use the platform AI keys via Google sign-in or `ALLOWED_SIGNUP_EMAILS`.
   - *(Optional)* `FREE_PLAN_DAILY_AI_CAP` — Daily ceiling on Free-plan AI requests across all accounts.
   - *(Optional)* `STRIPE_SECRET_KEY`, `STRIPE_PRICE_PRO_MONTHLY`, `STRIPE_PRICE_PRO_YEARLY`, `STRIPE_WEBHOOK_SECRET` — Enables paid Pro (see **Billing** above).
   - `SITE_OPERATOR`, `SUPPORT_EMAIL`, `SITE_JURISDICTION` — Shown on the Terms and Privacy pages.
   - *(Recommended)* `BLOB_READ_WRITE_TOKEN` — Create a **private** Vercel Blob store for uploaded chart images.
   - *(Optional)* `ADMIN_EMAILS` — Extra admins for `/admin`.
   - *(Optional)* `GOOGLE_CLIENT_ID` + `GOOGLE_CLIENT_SECRET` — Enables "Continue with Google" (redirect URI `<BETTER_AUTH_URL>/api/auth/callback/google`).
   - *(Optional)* `DEEPSEEK_API_KEY`, `GEMINI_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`.
3. **Deploy**: Migrations and the curated Academic IELTS starter bank are applied idempotently on first request.

### Upgrading a single-user (passcode) deployment

1. Set `OWNER_EMAIL` to the email you will sign up with, plus `BETTER_AUTH_SECRET` and `BETTER_AUTH_URL`.
2. Set `APP_SECRET`. If you never defined it before, also keep `APP_PASSCODE` set until you have opened Settings and pressed Save once: keys encrypted with the old passcode are then re-encrypted with `APP_SECRET`.
3. Deploy and sign up with that email, then verify it: through the emailed link (with email configured), by signing in with Google, or manually with `UPDATE user SET email_verified = 1 WHERE email = '<OWNER_EMAIL>';`. On your next page load your existing essays, evaluations, Vault, drills, custom prompts, and settings move to your account. Until then the data is held by a placeholder owner and is visible to no one. If someone else registered your address first, delete that account in the database (`DELETE FROM user WHERE email = '...'`) and sign up again.
