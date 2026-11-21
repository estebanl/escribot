# Data API

The non-AI routes of the API: paths, methods, status codes and JSON. The code is in `Escribot.Api/Api` and
`Escribot.Api/Common`. Sign-in is described in [sign-in.md](sign-in.md) and the
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
| `GET` `PUT /api/settings` | `GET` lists every provider (`providers`: whether it is configured, meaning AI requests can use it (the user saved a key for it, or the platform has a key for it and the user has no key of their own), its masked saved key, whether the platform has a key, its models and suggested models), the model each role uses (`settings.models.{grader,fast,vision}` as `{provider, model}`), usage and billing status. `PUT` takes `models` in the same shape; it still accepts the older `activeProvider`, `graderModel`/`fastModel`/`visionModel` and `apiKeysToUpdate` |
| `PUT` `DELETE /api/settings/providers/{id}` | `PUT {apiKey?, models?}` adds a provider or changes its key or model list and answers the provider as `GET /api/settings` lists it. Adding one needs a key unless the platform has its own and the user has no key of their own; without `models` a new provider starts with the `starting` list of its models route when that list was loaded in the last hour, else its suggested models. `DELETE` (204) removes the key and the list. After either, every role on a provider that is no longer configured moves to a configured one (the provider just saved, for `PUT`), and every role whose model its provider no longer lists moves to one it does: the model picked for the role (see the models route), else its first model. An unknown provider is a 404 |
| `POST /api/settings/providers/{id}/models` | `{apiKey?}` answers `{models: [{id, name}], starting, roles: {grader, fast, vision}}`: the models the provider offers now, newest first, from its own list-models API (OpenAI, Anthropic, Gemini and DeepSeek, filtered to chat models) or OpenRouter's public catalogue. It lists with `apiKey` when given (the setup form sends the key it is about to save), else the user's saved key for the provider, else the platform's. Lists are cached on the server for an hour per provider and key. No key is a 400, a key the provider rejects is a 400, a provider that fails is a 502, more than 30 calls in 10 minutes is a 429, and an unknown provider is a 404. It is a `POST` so a pasted key never appears in a URL. `roles` are picked automatically (`Ai/ModelPicker.cs`) by price, from OpenRouter's public catalogue, among the eight newest models: `fast` is the newest cheap one (within 1.5× of the cheapest), `grader` the newest above that and at most the median price, `vision` the newest cheap one that reads images. `starting` is those, then the newest, up to five. With too few prices to pick (offline, or model names OpenRouter does not use, as with DeepSeek) the configured defaults are used where the provider still offers them, else its newest model; OpenRouter itself always uses its configured defaults. A provider's `suggestedModels` are the fallback when the live list cannot be loaded |
| `GET /api/images/{id}` | Served to the owner only |
| `GET` `POST /api/backup` | Download and restore of the writing data |
| `GET /api/account/export` | Everything stored about the user; 5 an hour |
| `POST /api/errors` | Browser error reports; public; 10 a minute per user or address |
| `POST /api/billing/checkout`, `/portal`, `GET /api/billing/prices`, `POST /api/stripe/webhook` | See [billing.md](billing.md) |
| `GET /api/admin/overview`, `PATCH /api/admin/users/{id}` | Admins only; 404 for everyone else. See [billing.md](billing.md) |
| `GET /api/viewer` | The signed-in person with plan, `canUpgrade`, `isAdmin` and `onboarded`, or `{ "user": null }`; public |
| `GET /api/config` | `SITE_OPERATOR`, `SUPPORT_EMAIL`, `SITE_JURISDICTION` for the legal pages; public |
| `GET /api/pages/studio`, `/write/{id}`, `/analytics`, `/settings`, `/drills` | The data of the browser app's pages, in the shape of the props its component takes. `/write/{id}` is 404 for a session that is not the user's |

Every route needs a session except `/api/errors`, `/api/billing/prices`, `/api/viewer`,
`/api/config` and `/api/stripe/webhook` (see `ApiGuard`). 

## Wire format

The browser app receives one flat object per record: nested values are JSON text in fields
ending in `Json` (`chartSpecJson`, `criterionFeedbackJson`, …). The documents hold those
values as real JSON, and `Wire` turns each document into the flat shape. Timestamps are UTC
with three decimals.

## Validation

The routes read a body the forgiving way: a field of the wrong type counts as missing, and
the route then answers with its own message. On top of that:

- A session `mode`, a prompt `taskType`, a settings `nativeLanguage` and `activeProvider`
  must be known values.
- `apiKeysToUpdate` and the provider routes accept only the five known providers. API keys
  are at most 500 characters.
- A model name is at most 200 characters, and an OpenRouter model has a slash
  (`vendor/model`) while no other provider's does. A role's `{provider, model}` must pass
  that check, and a provider lists 1 to 20 models (duplicates are dropped).
- Bands are 0 to 9. Prompt and Vault texts are limited to 20,000 characters, titles and
  the like to 300.
- A backup file is checked row by row. It must hold JSON in its `…Json` fields and dates
  in its date fields, and at most 20,000 rows.
- A body that is not JSON is a 400 `{"error":"The request could not be read."}`.

## Images

- Uploads arrive as data URLs: PNG, JPEG, WebP or GIF, at most 2 MB each. The bytes
  must start with the declared format's signature; an empty image or one that does not
  match its declared type is a 400.
- The total per user is limited by plan: 25 MB on Free, 250 MB on Pro, unlimited for the owner.
- The bytes go to the `files` blob container as `users/{userId}/images/{id}.{ext}`; the
  description is an `image` document in the user's partition. The AppHost declares the
  container, for the emulator and for Azure.
- An image is found through its owner's partition, so another user's id is a 404.
- Deleting the account deletes the blobs.

## Saved API keys

AES-256-GCM with the SHA-256 of `App:EncryptionSecret` as the key, stored as
`iv:tag:ciphertext` in Base64. A key is never returned,
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
`Common/ChartSpecNormalizer.cs` mirror `client/src/lib/ielts-utils.ts` and
`client/src/lib/chart-utils.ts`, for the AI endpoints to use. Both have unit tests.

## The browser app

`client/` is built into `wwwroot` (in the Docker image) and served by `Hosting/ClientApp.cs`:
files as they are (`/assets/*` cached for a year, the rest revalidated), and `index.html` for
every other address that is not under `/api`. Before sending the page it applies the checks
the pages make in the browser, so the status is right: signed-out visitors are redirected to
`/login?next=...` (except on the public pages), and `/admin` for non-admins, another user's
`/write/{id}` or `/session/{id}`, and unknown addresses answer 404. An unmatched `/api` address
is a JSON 404, never the page.
