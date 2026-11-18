## Summary & Motivation

<!-- Briefly describe what this PR changes and why. -->

Fixes #<!-- issue number, or remove if not applicable -->

## Scope & Stack Touched

- [ ] **Next.js Web App** (`src/`) — UI components, pages, client state, or Next.js API routes
- [ ] **.NET 11 Server** (`server/`) — ASP.NET Core minimal API, Cosmos DB repositories, Blob Storage, or Aspire AppHost
- [ ] **Database / Data Model** — Drizzle SQLite/Turso schema (`src/lib/db/schema.ts` + `/drizzle`) or Cosmos DB containers/models (`server/src/Shared/DataModelSchema.cs`)
- [ ] **AI & IELTS Grading / Coaching** — OpenRouter/provider routing, rubric evaluation, outline check, micro-drills, or prompt generation
- [ ] **Auth, Billing & Quotas** — Sign-in/session handling, Stripe Checkout/webhooks, plan limits, or rate limiting
- [ ] **CI / Infrastructure / Docs** — GitHub Actions (`.github/workflows/`), Docker (`server/Dockerfile`), or documentation (`README.md`, `server/docs/`)

## API Contract & Next.js ↔ .NET Parity

<!-- Skip or mark N/A if this PR does not touch API routes or data models. -->

- [ ] **Wire compatibility preserved** (or N/A): Route paths, HTTP methods, status codes, JSON property names (`*Json` string fields vs. nested objects via `Wire`), and 3-decimal UTC timestamps match between Next.js (`src/app/api/`) and .NET (`server/src/FluentEscritor.Api/`).
- [ ] **Documentation updated** (or N/A): Changes to endpoints, auth flows, or Cosmos DB containers are reflected in `server/docs/` (`data-model.md`, `data-api.md`, `sign-in.md`) and `README.md`.

## Security & Tenant Isolation Checklist

- [ ] **Tenant scoping & 404 semantics**: User-owned data is strictly scoped by `userId` (`src/lib/db/scope.ts` in Next.js; `/userId` partition key in Cosmos DB). Accessing or mutating another user's resource returns `404` (never `403`), and signed-out API requests return `401`.
- [ ] **Zero hardcoded secrets**: No secrets, tokens, or fallback keys are committed in code, config, or tests (verified by `gitleaks` in CI). User-saved API keys remain `AES-256-GCM` encrypted and are never returned in plaintext.
- [ ] **Safe error & log handling**: Exceptions and error events (`error_events` / `errors` container) never leak stack traces to clients, redact credentials/tokens, and never log user essay or prompt prose.
- [ ] **Quotas & rate limits**: Any new AI or public/auth endpoints enforce fixed-window rate limits, monthly plan quotas (`Free` vs `Pro` vs own-key), and daily Free AI caps where applicable.
- [ ] **Account export & deletion**: Any new user-owned entity or blob is included in `/api/account/export` (excluding raw secrets/keys) and is cleanly purged upon account deletion.

## Verification & Test Plan

### Automated Checks (mirrors `.github/workflows/ci.yml`)

- [ ] **Next.js Lint, Type-Check & Build**:
  ```bash
  npm run lint && npx tsc --noEmit && npm run build
  ```
- [ ] **Next.js Tenant Isolation Suite** (against a running server):
  ```bash
  BASE_URL=http://localhost:3000 npm run test:isolation
  ```
- [ ] **.NET Build & xUnit Tests** (`server/`):
  ```bash
  cd server && dotnet test --configuration Release
  ```
- [ ] **Docker Image & Secret Guard** (if `server/` or `Dockerfile` changed):
  ```bash
  cd server && docker build --tag fluentescritor-api:ci .
  ```

### Manual Verification Steps

<!-- Describe how to manually test these changes (e.g., Coach Studio vs. Inspera Exam Simulator, Free vs. Pro account behavior). -->

1. 
2. 

## Screenshots / UI Recordings

<!-- Include Before/After screenshots or a short screen recording for any UI changes. Remove or write N/A for backend-only changes. -->

| Before | After |
| :---: | :---: |
| <!-- image/video --> | <!-- image/video --> |

## Deployment, Environment & Migration Notes

<!-- Note any action required when deploying to Vercel or Azure. Check N/A if none. -->

- [ ] **No special deployment steps required (N/A)**
- [ ] **Environment variables**: Added or updated variables in `.env.example`, `README.md`, and `server/` configuration (`App__*` / `ConnectionStrings__*`).
- [ ] **Drizzle SQL migration** (`Next.js`): Generated and committed new SQL migration in `/drizzle` via `npm run db:generate`.
- [ ] **Cosmos DB / Blob Storage** (`.NET`): Updated container/partition/TTL definitions in `server/src/Shared/DataModelSchema.cs` or seed data in `curated-prompts.json`.
