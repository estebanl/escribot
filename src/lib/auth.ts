import { betterAuth } from "better-auth";
import { APIError } from "better-auth/api";
import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { nextCookies } from "better-auth/next-js";
import { getDb, schema, type Db } from "@/lib/db";
import { provisionNewUser } from "@/lib/db/scope";
import { isEmailConfigured, isPasswordResetAvailable, sendEmail } from "@/lib/email";

export function isGoogleSignInEnabled(): boolean {
  return Boolean(
    process.env.GOOGLE_CLIENT_ID?.trim() && process.env.GOOGLE_CLIENT_SECRET?.trim()
  );
}

/** Optional comma-separated allowlist; when empty, anyone may sign up. */
function isSignupAllowed(email: string): boolean {
  const allowed = (process.env.ALLOWED_SIGNUP_EMAILS || "")
    .split(",")
    .map((e) => e.trim().toLowerCase())
    .filter(Boolean);
  if (allowed.length === 0) return true;
  const owner = process.env.OWNER_EMAIL?.trim().toLowerCase();
  const candidate = email.trim().toLowerCase();
  return allowed.includes(candidate) || candidate === owner;
}

function createAuth(db: Db) {
  const emailEnabled = isEmailConfigured();

  return betterAuth({
    appName: "FluentEscritor",
    secret: process.env.BETTER_AUTH_SECRET || process.env.APP_SECRET,
    database: drizzleAdapter(db, {
      provider: "sqlite",
      schema: {
        user: schema.user,
        session: schema.session,
        account: schema.account,
        verification: schema.verification,
        rateLimit: schema.rateLimit,
      },
    }),
    // Per-IP limits on the auth endpoints, stored in the database so they hold
    // across serverless instances. Sign-in, sign-up and password reset get
    // tighter limits than the session checks every page performs.
    rateLimit: {
      enabled: process.env.NODE_ENV === "production" || process.env.AUTH_RATE_LIMIT === "on",
      storage: "database",
      window: 60,
      max: 100,
      customRules: {
        "/sign-in/email": { window: 60, max: 5 },
        "/sign-up/email": { window: 3600, max: 10 },
        "/request-password-reset": { window: 3600, max: 3 },
        "/reset-password": { window: 600, max: 5 },
      },
    },
    emailAndPassword: {
      enabled: true,
      minPasswordLength: 8,
      // Only enforce verification when we can actually deliver the email.
      requireEmailVerification: emailEnabled,
      // Without a way to deliver the link, leave reset disabled rather than
      // pretend an email was sent.
      ...(isPasswordResetAvailable()
        ? {
            sendResetPassword: async ({ user, url }: { user: { email: string }; url: string }) => {
              await sendEmail({
                to: user.email,
                subject: "Reset your FluentEscritor password",
                text: `Open this link to choose a new password:\n\n${url}\n\nIf you did not request this, you can ignore this email.`,
              });
            },
          }
        : {}),
    },
    emailVerification: {
      // In development the link is printed to the console (see sendEmail).
      sendOnSignUp: emailEnabled || process.env.NODE_ENV !== "production",
      autoSignInAfterVerification: true,
      sendVerificationEmail: async ({ user, url }) => {
        await sendEmail({
          to: user.email,
          subject: "Verify your FluentEscritor email",
          text: `Welcome to FluentEscritor. Confirm your email address by opening this link:\n\n${url}`,
        });
      },
    },
    socialProviders: isGoogleSignInEnabled()
      ? {
          google: {
            clientId: process.env.GOOGLE_CLIENT_ID!.trim(),
            clientSecret: process.env.GOOGLE_CLIENT_SECRET!.trim(),
          },
        }
      : undefined,
    session: {
      expiresIn: 60 * 60 * 24 * 30,
      updateAge: 60 * 60 * 24,
    },
    databaseHooks: {
      user: {
        create: {
          before: async (user) => {
            if (!isSignupAllowed(user.email)) {
              throw new APIError("FORBIDDEN", {
                message: "Sign-ups are currently invite-only.",
              });
            }
          },
          after: async (user) => {
            await provisionNewUser(db, {
              id: user.id,
              email: user.email,
              emailVerified: user.emailVerified,
            });
          },
        },
      },
    },
    // Must stay last so server-side auth calls can set cookies.
    plugins: [nextCookies()],
  });
}

let authInstance: ReturnType<typeof createAuth> | null = null;

/** Lazily built so the database is migrated before Better Auth touches it. */
export async function getAuth() {
  if (!authInstance) {
    authInstance = createAuth(await getDb());
  }
  return authInstance;
}
