# Data model

How FluentEscritor stores its data in Azure Cosmos DB (NoSQL API, serverless), and the
repository layer the rest of the server uses. It replaces the 16 relational tables in
`src/lib/db/schema.ts`. This is a redesign for a document database, not a table-by-table port.

## Summary

- One database, `fluentescritor`, with five containers.
- Everything a user owns is in **one logical partition** of the `users` container, keyed by
  user id. Every request for a user's data is a single-partition operation, and tenant
  isolation is the partition boundary rather than a `WHERE user_id = ?` each query must remember.
- The few lookups by another key (email, session token, Stripe customer id) are small
  documents read by id.
- Rate limits and the daily Free AI cap are counters incremented atomically on the server.
- Error events and expired sessions are removed by time-to-live, not by application code.
- The Cosmos DB SDK is used only inside `FluentEscritor.Api/Data/Cosmos`. Everything else
  depends on the interfaces in `Data/Repositories.cs` and the records in `Data/Models`.

The database, the containers, their partition keys and time-to-live are defined once, in
`src/Shared/DataModelSchema.cs`. That file is compiled into the AppHost, which creates them
in the local emulator and emits them in the Azure infrastructure definitions, and into the
API, which reads and writes them. The app never creates containers itself, so in Azure its
managed identity needs data access only.

## Containers

| Container | Partition key | Time-to-live | Holds |
|---|---|---|---|
| `users` | `/userId` | off | Everything owned by one user |
| `shared` | `/pk` | off | Data shared by all users: the curated prompt bank |
| `lookups` | `/id` | per document | Email, session token and Stripe customer id → user id |
| `counters` | `/id` | per document | Rate-limit windows and the daily Free AI count |
| `errors` | `/day` | 30 days | Server and browser error events |

All containers use the default indexing policy. Cost is per request (serverless); there is
no provisioned throughput and an empty container costs nothing.

## `users`: one partition per user

Each document has three storage fields next to its own data:

- `userId`: the owner, and the partition key.
- `type`: what the document is.
- `id`: `type:entityId`, or just the type name for documents that exist once per user.
  The prefix lets a session and a draft share an entity id without colliding.

| `type` | `id` | Replaces table | Notes |
|---|---|---|---|
| `account` | `account` | `user`, `account` | Email, name, verified flag, password hash, external logins. Sign-in (issue 10) builds its user store on it. |
| `settings` | `settings` | `user_settings` | Created with defaults on first read. Encrypted API keys are a map, not JSON text. |
| `subscription` | `subscription` | `subscriptions` | One Stripe customer per user. `lastEventAt` guards against stale webhook events. |
| `prompt` | `prompt:{id}` | `prompts` (own rows) | The user's custom and AI-generated prompts. |
| `session` | `session:{id}` | `writing_sessions` | |
| `draft` | `draft:{id}` | `drafts` | Carries `sessionId`. |
| `evaluation` | `evaluation:{id}` | `evaluations` | Carries `draftId` and a copy of `sessionId`. |
| `vaultItem` | `vaultItem:{id}` | `vault_items` | |
| `drill` | `drill:{id}` | `micro_drills` | |
| `aiUsage` | `aiUsage:{id}` | `ai_usage` | Token counts and cost only; never prompt or essay text. |
| `image` | `image:{id}` | `images` | Metadata. The bytes are in Blob Storage under `blobName`. |

What changed from the relational shape:

- **JSON is JSON.** Columns that held JSON as text (`chart_spec_json`, `outline_json`,
  `criterion_feedback_json`, …) are nested objects. The data layer does not interpret them.
- **Drafts and evaluations stay separate documents** instead of being embedded in the
  session. An evaluation is large and a session can have several drafts, so embedding would
  rewrite a growing document on every save and move towards the 2 MB document limit.
  Because they share the session's partition and carry `sessionId`, a session with all its
  drafts and evaluations is still **one query**, and deleting it is **one transaction**
  (see [Concurrency](#concurrency) for sessions with 100 documents or more).
- **No join tables or foreign keys.** Ownership is the partition.
- **Timestamps** are UTC strings with exactly three decimals (`2026-11-15T18:04:05.123Z`),
  the format the existing API already returns, so they sort correctly as strings.

### Queries served

All single-partition unless stated.

| Need | How |
|---|---|
| Read one document the user owns | Point read by `id` and `userId` |
| Settings, creating defaults on first use | Point read; create if missing |
| List sessions, drafts, evaluations, vault items, drills, images | `type = X`, newest first |
| A session with its drafts and evaluations | One query on `sessionId` |
| Delete a session and everything under it | One transactional batch per 100 documents, the session document last |
| Monthly AI quota | `COUNT` of `aiUsage` since a date, filtered by feature and key source |
| Storage used | `SUM(bytes)` of `image` |
| Export everything | `SELECT * FROM c` in the partition |
| Delete everything | Batch-delete every id in the partition |
| Admin page: accounts, sign-ups, AI cost by plan, subscribers | **Cross-partition** queries filtered by `type`. Rare, admin-only, and the only cross-partition reads of this container. |

## `shared`: curated prompts

Curated prompts (rows with a null `user_id` today) live in one partition, `pk = "curated-prompts"`,
with the prompt id as the document id. A user's visible prompts are this partition plus the
`prompt` documents in their own partition: two single-partition queries.

The list ships with the app as `Data/Seed/curated-prompts.json`, exported from
`src/lib/db/seed-prompts.ts`. It holds the 14 original prompts only; the entries transcribed
from a published exam book are not in it, and a test fails if one returns.

On startup the API makes the stored list equal to the shipped one: it adds new prompts,
rewrites changed ones and deletes ones that are no longer shipped. Each stored prompt keeps
a hash of its seed entry, so an unchanged list costs one query and no writes, which matters
for an app that starts from zero often. Seeding runs in the background and retries, so a
database that is not reachable yet does not stop the app from starting.

Users cannot write to this container: the prompt repository rejects a save with source
`curated`, and user saves always go to the user's partition.

## `lookups`: finding a user by another key

One document per key: `id = kind:sha256(key)`, which is also the partition key, so a lookup
is one point read.

| Kind | Key | Used for |
|---|---|---|
| `email` | Lower-cased email | Sign-in, and making emails unique |
| `session` | Session token | Validating a session cookie; expires by `ttl` |
| `stripe` | Stripe customer id | Finding the user for a Stripe webhook |
| `verify-email` | Token from a verification email | Verifying an address; expires by `ttl` after an hour |
| `reset-password` | Token from a reset email | Choosing a new password; single use, expires by `ttl` after an hour |

- **Uniqueness.** Creating a document fails if the id exists. Creating an account writes the
  email lookup first, as a reservation (`pending: true`), then the account, then confirms
  the lookup by removing `pending`. The sign-up whose confirmation succeeds owns the address.
- **Keys are hashed.** Emails can contain characters that are not allowed in an id, and a
  session token must not be stored at all: a copy of the database does not give anyone a
  working session.
- **No cross-partition transaction.** A lookup and the account it points to are separate
  writes. If account creation stops after the lookup is written, the reservation stays
  pending. A later sign-up with that email takes it over only if it is still pending, has no
  account and is older than two minutes; a younger one is treated as a sign-up in progress
  and the address is reported as taken. Confirming and taking over are both ETag-conditional
  writes to the lookup, so exactly one succeeds: a sign-up that was merely slow fails to
  confirm, removes the account it wrote and reports the address as taken. If the account was
  written but never confirmed, the next sign-up confirms it instead.
- **Stripe customer.** The subscription document is written first, then the lookup. Creating
  a subscription that already exists checks the lookup and writes it if it is missing, so a
  retry repairs a half-finished attempt.
- **Emailed tokens** are used up by deleting their document: of several requests with the
  same token, only the one whose delete finds the document gets the user.
- Each lookup carries `userId`, so deleting a user can find and remove all of theirs.

## `counters`: atomic counters

A counter is one document per key **and time window**: `id = rate:sha256(key):windowStart`.
The window start is part of the id, so a new window is a new document that starts at one.
Counting a request is a single server-side patch (`increment /count by 1`); if the document
does not exist yet it is created, and if two requests race to create it the loser patches
instead. Two concurrent requests can never read the same count, which is the property the
SQL upsert in `src/lib/rate-limit.ts` provided. Finished windows expire by `ttl`.

The global daily cap on Free AI usage is the same mechanism with `id = free-ai:yyyy-MM-dd`.
It replaces a count over every user's usage rows, which here would be a cross-partition query
on every Free request.

Rate-limit keys are hashed because they can contain IP addresses, which are not kept.

## `errors`: error events

Partitioned by UTC day, with a container-level time-to-live of 30 days. This replaces the
occasional manual cleanup of `error_events`.

Before an event is stored it is shortened (message 1,000 characters, stack 4,000) and
anything shaped like a credential is replaced with `[redacted]`: bearer tokens, API keys,
named secrets such as `password=…`, JSON web tokens, connection-string keys and long opaque
values. Only the path of a URL is kept, not its query string. An event has no field for
essay text, and callers must not put any in a message; the filter cannot recognise prose.

The admin view reads the newest events and a count for the last 24 hours. Both are
cross-partition queries over at most 31 small partitions.

## Tenant isolation

- Every repository method that touches user data takes the user id and uses it as the
  partition key. There is no method that reads a user document without one, apart from the
  admin repository.
- A document id that belongs to another user is simply not found: reads return null, deletes
  and updates report nothing changed.
- On save, the user id argument decides the owner. A `userId` on the incoming record is
  overwritten, so a forged one cannot move a document into someone else's partition.
- Ids come from the browser, so they are validated before use (length, and no `/ \ ? # :` or
  whitespace). An invalid id is treated as not found on read and rejected on write.

`TenantIsolationTests` checks all of this against the emulator for every document type.

## Deleting and exporting a user

**Export** is one query over the user's partition, grouped by type. The password hash,
security stamp and encrypted API keys are left out; the names of the providers with a saved
key are listed instead.

**Delete** runs in this order. Until step 3 the account, its email lookup and
the session still exist, so if it fails the user can sign in and delete again:

1. Delete every document in the user's partition except the account, in transactional
   batches of 100. This covers document types added later without changing the code.
2. Remove the user id from their error events, which are kept for debugging until they expire.
3. Delete the user's lookups (sessions, Stripe customer, emailed tokens), the email lookup
   last, and then the account document.

Blobs and the Stripe customer are outside this layer. `AccountDeletion` (in `Auth/`) deletes
the user's image blobs first and then calls this; deleting the Stripe customer is added
there with billing.

## Concurrency

- Read-modify-write updates (settings, account, subscription) use the document's ETag: the
  write succeeds only if nobody else wrote in between, otherwise it starts again from the
  newer version. Two requests changing settings at once cannot lose each other's change.
- Subscription updates pass a function that can return the record unchanged, which is how a
  stale Stripe event is skipped without a write.
- Writes inside one partition that must happen together (deleting a session, saving several
  vault items) use a transactional batch. A batch holds at most 100 operations. A session
  with more documents than that is deleted in several batches with the session document in
  the last one, so a failure part way leaves the session in place and deleting it again
  finishes the job; it is never left as drafts or evaluations without a session.

## Repository interfaces

Defined in `FluentEscritor.Api/Data/Repositories.cs`.

| Interface | Purpose | Equivalent today |
|---|---|---|
| `IUserAccountRepository` | Accounts, unique by email | Better Auth `user`, `account` |
| `IAuthSessionStore` | Session token → user | Better Auth `session` |
| `IOneTimeTokenStore` | Emailed verification and reset tokens | Better Auth `verification` |
| `ISettingsRepository` | `GetOrCreateAsync`, `UpdateAsync` | `getOrCreateSettings` |
| `ISubscriptionRepository` | Billing state, lookup by Stripe customer | `src/lib/billing/subscription.ts` |
| `IPromptRepository` | `ListVisibleAsync`, `GetVisibleAsync`, own prompts | `visiblePrompts`, `getVisiblePrompt` |
| `IWritingRepository` | Sessions, drafts, evaluations | `getOwnedSession` and the session routes |
| `IVaultRepository`, `IDrillRepository`, `IImageRepository` | Vault items, drills, image metadata | |
| `IAiUsageRepository` | Usage records and the quota count | `src/lib/ai/access.ts` |
| `IRateLimiter` | Fixed-window rate limit | `checkRateLimit` |
| `IFreeAiDailyCounter` | Daily Free AI cap | Count in `src/lib/ai/access.ts` |
| `IErrorEventRepository` | Error events | `src/lib/errors.ts` |
| `IUserDataRepository` | Export, restore and delete everything for a user | `src/lib/account.ts`, the export route |
| `IAdminRepository` | Platform-wide figures | Queries in `src/app/admin/page.tsx` |
| `ICuratedPromptStore` | Used only by the startup seeding | `migrateAndSeed` in `src/lib/db/index.ts` |

## Not carried over

- `rate_limit` (Better Auth's own counters): replaced by `IRateLimiter`.
- The legacy single-user data claim and `LEGACY_USER_ID`: there is no live data.
- Inline image bytes in the database (`data_base64`): images are always in Blob Storage.

## Open points for review

1. **Changing an email address** is not supported by `IUserAccountRepository.UpdateAsync`.
   It needs the new lookup created before the old one is removed; add it with issue 10 if
   the product needs it.
2. **A user's own prompt with the same id as a curated prompt** is hidden by the curated
   one. Ids are generated by the app, so this should not happen; issue 11 can reject it at
   the API if wanted.
3. **Admin queries are cross-partition.** Fine at the expected scale. If the user count grows
   large, keep running totals in `shared` instead.

## Tests

`dotnet test` in `server/` starts the AppHost with the Cosmos DB emulator through Aspire's
testing support and runs the repositories against it. A container runtime is required. All
test data is synthetic. To run only the tests that need no containers:

```bash
dotnet test --filter "Category!=Integration"
```
