# Billing and admin

Stripe Checkout, the customer portal, the Stripe webhook and the admin endpoints, ported
from `src/lib/billing/`, `src/app/api/billing/`, `src/app/api/stripe/webhook/`,
`src/app/api/admin/users/[id]/` and the queries of `src/app/admin/page.tsx`. The code is in
`FluentEscritor.Api/Billing` and `FluentEscritor.Api/Api/{Billing,Admin}Endpoints.cs`.

## Configuration

| Setting | |
|---|---|
| `STRIPE_SECRET_KEY` | API key (`sk_test_...` locally) |
| `STRIPE_PRICE_PRO_MONTHLY` | Monthly Pro price id |
| `STRIPE_PRICE_PRO_YEARLY` | Optional yearly Pro price id |
| `STRIPE_WEBHOOK_SECRET` | Webhook signing secret (`whsec_...`) |
| `STRIPE_API_BASE` | Optional, ignored in Production: point at stripe-mock or a fake server |

Billing is on only when the key, the monthly price and the webhook secret are all set
(`StripeSettings.Enabled`). Otherwise checkout and portal answer 503 and the prices are
empty. The webhook needs only the key and the secret.

## Routes

| Route | Notes |
|---|---|
| `POST /api/billing/checkout` | `{ interval?: "month" \| "year" }` → `{ url }`. 10 per 10 minutes. 400 for the owner or an interval not offered, 409 `already_subscribed` when the user already has Pro access, 502 when Stripe fails |
| `POST /api/billing/portal` | → `{ url }`. 404 until the user has a subscription |
| `GET /api/billing/prices` | Public. `{ billingEnabled, prices: { month?, year? }, viewer, hasSubscription }`; prices are `{ unitAmount, currency, interval }` in minor units and kept for an hour |
| `POST /api/stripe/webhook` | Public, verified by `Stripe-Signature`. 400 for a bad signature, 503 when not configured, 500 (so Stripe retries) when applying fails |
| `GET /api/admin/overview?q=` | Users (100 newest, `q` filters by email), sign-ups, subscribers by status, platform AI cost by plan, essays graded, errors in the last 24 hours and the 25 newest errors. Month to date, UTC |
| `PATCH /api/admin/users/{id}` | `{ plan: "pro" \| "free" }` grants or removes complimentary Pro. A paid subscription is unaffected |

The admin routes answer 404 `Not found.` to anyone who is not an admin (the verified owner
or a verified address in `ADMIN_EMAILS`), before the body is read.

## One customer per user

`SubscriptionSync.GetOrCreateCustomerAsync` creates the customer with the idempotency key
`customer-create-{userId}` and stores it with `ISubscriptionRepository.CreateAsync`, which
keeps whichever customer was stored first. Checkout puts `userId` in the subscription's
metadata, so a customer made in the dashboard can still be matched to its user.

## Webhook

Applied events: `customer.subscription.created`, `.updated`, `.deleted`, `.paused`,
`.resumed`. Others are answered `{ received: true, handled: false }`. The result of an
applied event is one of:

- `applied`: the subscription state was stored.
- `stale`: an event for the same subscription older than the last one applied (`lastEventAt`).
- `ignored`: an event for a second subscription on the customer, which takes over only if
  it grants access and the tracked one does not, so the end of an old subscription never
  downgrades someone who subscribed again.
- `unknown_customer`: no user has this customer, and the metadata names no existing user.

`active`, `trialing` and `past_due` grant Pro. Every other status is Free, unless the user
has complimentary Pro or is the owner (`PlanResolver`).

## Account deletion

`AccountDeletion` deletes the Stripe customer before anything else: that cancels any
subscription at once and removes saved cards, while invoices stay in Stripe. A customer
Stripe no longer has counts as deleted. If Stripe cannot be reached, nothing is deleted and
`/api/auth/delete-user` answers 502 `BILLING_UNAVAILABLE`.

## Trying it locally with the Stripe CLI

1. In test mode, create a Pro product with a monthly (and optionally yearly) price.
2. `stripe login`, then `stripe listen --forward-to https://localhost:<port>/api/stripe/webhook`
   (add `--skip-verify` for the development certificate). It prints the `whsec_...` secret.
3. Set the four `STRIPE_*` settings above as user secrets of `FluentEscritor.AppHost`
   (`dotnet user-secrets set STRIPE_SECRET_KEY sk_test_... --project src/FluentEscritor.AppHost`),
   which passes them on to the API, and run the AppHost.
4. Sign in, `POST /api/billing/checkout`, and pay with card `4242 4242 4242 4242`. The
   `stripe listen` window shows `customer.subscription.created` answered with `applied`,
   and `GET /api/settings` shows the subscription.
5. `POST /api/billing/portal` and cancel: `customer.subscription.updated` (and, at the end
   of the period or with `stripe trigger`, `.deleted`) follow.
