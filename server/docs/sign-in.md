# Sign-in

How accounts, sessions and email work, under `/api/auth/...`. The code is in `Escribot.Api/Auth` and
`Escribot.Api/Email`.

## Parts

| Part | What it does |
|---|---|
| ASP.NET Core Identity (`UserManager<AppUser>`) | Validates new users, hashes and checks passwords |
| `AccountUserStore` | The Identity user store, over `IUserAccountRepository` and so Cosmos DB |
| `SessionManager`, `SessionAuthenticationHandler` | The session cookie and its check on every request |
| `ApiGuard` | Requires a session on every `/api` route unless it is public; refuses cross-site changes |
| `AccountService` | Creates accounts under the allowlist and gives them their starting data |
| `AccountDeletion` | Deletes a user's files, then their documents |
| `IEmailSender` | Sends the verification and reset emails |

## Endpoints

All under `/api/auth`. Errors are `{ "error": "...", "code": "..." }`.

| Method and path | Body or query | Result |
|---|---|---|
| `POST /sign-up/email` | `name`, `email`, `password`, `callbackURL?` | `{ user, verificationRequired }` |
| `POST /sign-in/email` | `email`, `password` | `{ user }`; 401 `INVALID_EMAIL_OR_PASSWORD`; 403 `EMAIL_NOT_VERIFIED` |
| `POST /sign-out` | | `{ success }` |
| `GET /me` | | `{ user }`, or 401 |
| `GET /verify-email` | `token`, `callbackURL?` | Redirect to `callbackURL`, signed in; `?error=invalid_token` on failure |
| `POST /send-verification-email` | `email`, `callbackURL?` | `{ success }` always |
| `POST /request-password-reset` | `email`, `redirectTo?` | `{ success }` always |
| `POST /reset-password` | `token`, `newPassword` | `{ success }`; 400 `INVALID_TOKEN` |
| `POST /delete-user` | `password` | `{ success }`; 400 `INVALID_PASSWORD` |
| `GET /config` | | `{ googleEnabled, passwordResetAvailable, emailVerificationRequired }` |
| `GET /sign-in/google` | `callbackURL?` | Redirect to Google |
| `GET /callback/google` | | Google's redirect URI; redirects to `callbackURL`, signed in |

`user` is `{ id, name, email, emailVerified, image, hasPassword, isOwner, isAdmin }`.

Google sign-in starts with a plain link to `GET /sign-in/google`, and the reset email links
straight to the `/reset-password?token=...` page.

## Sessions

- The cookie holds a random 256-bit token and nothing else. The session is a `lookups`
  document found by the SHA-256 of the token, so a copy of the database gives nobody a
  working session.
- A session lasts 30 days. When one is used more than a day after it was last extended,
  it and its cookie get 30 days again.
- Every request reads the session and its account. Signing out, resetting the password and
  deleting the account remove sessions on the server, so they end at once everywhere.
- The cookie is `HttpOnly`, `SameSite=Lax`, `Path=/`. Over HTTPS, and always in production,
  it is also `Secure` and named `__Host-escribot-session`.

## Rules

- **Passwords:** 8 to 128 characters, no other rule. Stored as Identity's PBKDF2 hash.
- **Email verification** is required only when email is configured. Until then a new
  account has no session and sign-in answers 403. Without email, outside production, the
  link is printed to the console and the account is signed in straight away.
- **Password reset** needs a way to deliver the link: email, or the console outside
  production. In production without email it is disabled and says so.
- **Allowlist:** with `ALLOWED_SIGNUP_EMAILS` set, only those addresses and `OWNER_EMAIL` can
  create an account, by password or through Google.
- **Owner and admins:** `OWNER_EMAIL` and `ADMIN_EMAILS` count only for a verified address.
  The session carries the roles `owner` and `admin`, and the `Admin` authorization policy
  requires the second.
- **Deleting an account** needs the password. An account without one (Google only) needs
  a session started within the last day instead. The Stripe customer is deleted first;
  if Stripe cannot be reached nothing is deleted and the answer is 502 with
  `code: "BILLING_UNAVAILABLE"`.
- **Rate limits per client address:** sign-in 5 a minute, sign-up 10 an hour, reset request
  3 an hour (the same for sending the verification email again), reset 5 in 10 minutes,
  and 100 a minute over all of `/api/auth`. They are always on, counted by `IRateLimiter`.
- **Signed-out API calls** get 401 with `{"error":"Unauthorized — please sign in."}`. Public:
  `/api/auth/...`, `/api/stripe/webhook`, `/api/errors`, `/api/billing/prices`.

## Links, enumeration and Google

- **Enumeration:** when verification is required, signing up with a taken address answers
  exactly as for a new one. Reset and resend always answer the same.
- **Links last an hour.** A reset link works once, and a reset also signs the account out
  everywhere and cancels its other reset links. A verification link signs in only the
  first time it is opened; opening it again just goes on to the app, because mail
  scanners often open links before the person does.
- **Google and existing accounts.** An account already linked to a Google identity is
  entered by that identity. Otherwise accounts are matched by email address, and only
  when Google itself knows who controls the address today: a Gmail address, or a Google
  Workspace account (it carries its domain in `hd`). For any other address Google checked
  the mailbox once, when the Google Account was made, so `email_verified` proves nothing
  now. Such a sign-in never enters or links an existing account (`/login?error=
  google_account_not_linked`), and a new account from it starts unverified and gets the
  verification email like any other. When email verification is required, no session is
  started and the callback redirects to `/login?verify=1` until the address is verified.
- **Google and an unverified account.** If the matching account was never verified, its
  password was set by someone who never proved they own the address: that password and
  its sessions are removed when a Google sign-in that does prove it takes the account over.
- **Cross-site requests:** a `POST`, `PUT`, `PATCH` or `DELETE` to `/api` with an `Origin`
  header must come from the app's own address (`App:PublicUrl` or the address requested).
- **Redirect targets** taken from a request (`callbackURL`, `redirectTo`) are used only if
  they are a path on this site.

## Email

`IEmailSender` has three implementations, chosen at startup:

| When | Sender |
|---|---|
| `AZURE_COMMUNICATION_CONNECTION_STRING` and `EMAIL_FROM` are set | Azure Communication Services |
| Neither, outside production | Prints the message to standard output (not to logs or telemetry) |
| Neither, in production | Sends nothing and logs a warning without the link |

- `EMAIL_FROM` must be a MailFrom address on a domain connected to the Communication
  Services resource. `Name <address>` is accepted and reduced to the address.
- A send returns once Azure has accepted the message; the app does not wait for delivery.
- A failed send is logged as `Email delivery failed (status)` with no other detail and
  does not fail the request, which would reveal whether the address has an account.
- **Azure sending limits:** 30 a minute and 100 an hour on a verified custom domain, 5 and
  10 on an Azure-managed domain. The app's limits are per client address, so many
  different visitors can still exceed them; ask Azure for higher limits before a launch.

## Google

On when `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` are both set. The redirect URI to
register is `<public URL>/api/auth/callback/google` (for local development through the
AppHost, `http://localhost:5173/api/auth/callback/google`, with both values set in the
AppHost's user secrets).

The state of the round trip to Google is sealed with a key derived from
`App:EncryptionSecret` (`SecretDataProtectionProvider`) instead of ASP.NET Core's data
protection key ring. A key ring would have to be stored and shared between instances
that start from nothing; every instance already has the secret. The correlation cookie
uses `SameSite=Lax` so the top-level GET callback works over both HTTPS and local HTTP.

## Behind the Azure Container Apps ingress

Set `App__TrustForwardedHeaders=true`. The client address for rate limits and the `https`
scheme for Google's redirect URI then come from `X-Forwarded-For` and `X-Forwarded-Proto`.
Only the last `X-Forwarded-For` entry, the one the ingress adds, is used. Do not set it
when clients can reach the app directly.

## Behind Cloudflare's proxy

There, the last `X-Forwarded-For` entry is a Cloudflare edge server, shared by many
visitors: rate limits keyed on it would let one abuser lock everyone out of sign-in.
Set `App__CloudflareIpRanges` to Cloudflare's CIDR ranges (the deployment does, from
`infra/cloudflare-ips.json`, when `BEHIND_CLOUDFLARE=true`). A request whose forwarded
address is in one of those ranges then takes its client address from `CF-Connecting-IP`
(`CloudflareClientAddress`). Any other request keeps the forwarded address and ignores
the header, since anyone could send it. The setting requires
`App__TrustForwardedHeaders=true`, and the ingress admits only Cloudflare's ranges, so
the proxy can't be skipped.

## Not done here

- **Changing an email address** is still not supported.
