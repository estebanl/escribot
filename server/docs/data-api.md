# Data API

The non-AI routes of the Next.js app on the .NET server, with the same paths, methods,
status codes and JSON. The code is in `FluentEscritor.Api/Api` and
`FluentEscritor.Api/Common`. Sign-in is described in [sign-in.md](sign-in.md) and the
stored documents in [data-model.md](data-model.md).

## Routes

| Route | Notes |
|---|---|
| `GET /api/sessions` | All sessions with their prompts, drafts and evaluations, plus the analytics block |
| `POST /api/sessions` | `mode`, `task1PromptId?`, `task2PromptId?`. 404 for a prompt the user cannot see |
| `GET /api/sessions/{id}` | 404 for a session that is not the user's |
| `GET /api/prompts` | The curated bank plus the user's own; `?taskType=` filters |
| `POST /api/prompts` | A custom prompt; a Task 1 prompt may carry `imageDataUrl` |
| `GET` `POST` `PATCH` `DELETE /api/vault` | As before; `DELETE` takes `?id=` |
| `GET /api/drills` | The 20 most recent. Generating and scoring come with the AI endpoints |
| `GET` `PUT /api/settings` | Includes key status, usage and billing status |
| `GET /api/images/{id}` | Served to the owner only |
| `GET` `POST /api/backup` | Download and restore of the writing data |
| `GET /api/account/export` | Everything stored about the user; 5 an hour |
| `POST /api/errors` | Browser error reports; public; 10 a minute per user or address |
| `POST /api/billing/checkout`, `/portal`, `GET /api/billing/prices`, `POST /api/stripe/webhook` | See [billing.md](billing.md) |
| `GET /api/admin/overview`, `PATCH /api/admin/users/{id}` | Admins only; 404 for everyone else. See [billing.md](billing.md) |

Every route needs a session except `/api/errors`, `/api/billing/prices` and
`/api/stripe/webhook` (see `ApiGuard`). The admin image migration is not ported: there are
no inline images to move.

## Same JSON over a different store

The Next.js routes returned database rows: nested values were JSON text in fields ending
in `Json` (`chartSpecJson`, `criterionFeedbackJson`, …). The documents now hold those
values as real JSON. `Wire` turns each document back into the old row, so the browser
code does not change. Timestamps are UTC with three decimals, as before.

## Validation

The routes read a body the forgiving way the originals did: a field of the wrong type
counts as missing, and the route then answers with its original message. On top of that:

- A session `mode`, a prompt `taskType`, a settings `nativeLanguage` and `activeProvider`
  must be known values. The originals stored whatever was sent.
- `apiKeysToUpdate` accepts only the five known providers.
- Bands are 0 to 9. Prompt and Vault texts are limited to 20,000 characters, titles and
  the like to 300.
- A backup file is checked row by row as the zod schemas did. It must also hold JSON in
  its `…Json` fields and dates in its date fields, because they are stored as such now,
  and at most 20,000 rows.
- A body that is not JSON is a 400 `{"error":"The request could not be read."}`.

## Images

- Uploads arrive as data URLs: PNG, JPEG, WebP or GIF, at most 2 MB each.
- The total per user is limited by plan: 25 MB on Free, 250 MB on Pro, unlimited for the owner.
- The bytes go to the `files` blob container as `users/{userId}/images/{id}.{ext}`; the
  description is an `image` document in the user's partition. The AppHost declares the
  container, for the emulator and for Azure.
- An image is found through its owner's partition, so another user's id is a 404.
- Deleting the account deletes the blobs.

## Saved API keys

AES-256-GCM with the SHA-256 of `App:EncryptionSecret` as the key, stored as
`iv:tag:ciphertext` in Base64: the format of `src/lib/crypto.ts`. The older fallbacks
(`APP_PASSCODE`, the built-in development secret) are gone. A key is never returned,
only whether one is saved and its first and last characters; exports name the provider only.

## Plans and usage

`PlanResolver` gives the plan: the verified owner, then Pro (a Stripe subscription that
is `active`, `trialing` or `past_due`, or `plan: "pro"` in settings), then Free.
`UsageService` reports this month's use for the Settings page. Enforcing the allowance
belongs to the AI endpoints.

## Errors

- `ApiErrorHandler` catches anything a route did not handle, records it as an error
  event with the method and route template (not the address, which can hold ids) and
  answers 500 with the route's original message, such as `Failed to load sessions.`
  Nothing about the exception reaches the caller.
- Stored events pass through the data layer's filter, which shortens them and removes
  anything shaped like a credential, and expire after 30 days.

## Restoring a backup is not one transaction

Every restored row gets a fresh id and belongs to the importing user, so a file cannot
overwrite anything. Up to 100 rows of moderate size are saved together; a larger file
is saved in several batches, so a failure part way leaves the earlier batches saved.
Restoring the same file again adds a second copy, as it did before.

## Shared helpers

`Common/Ielts.cs` (band rounding, the weighted exam band, word counts, task minimums) and
`Common/ChartSpecNormalizer.cs` are ports of `src/lib/ielts-utils.ts` and
`src/lib/chart-utils.ts`, for the AI endpoints to use. Both have unit tests.
