import { useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { AlertCircle, ArrowRight, CheckCircle2, Feather } from "lucide-react";
import { auth } from "@/lib/auth";

export type AuthMode = "login" | "signup" | "forgot" | "reset";

const COPY: Record<AuthMode, { intro: string; submit: string; busy: string }> = {
  login: {
    intro:
      "Sign in to pick up your Academic Task 1 & Task 2 drafts, Error Vault, and band progress on any device.",
    submit: "Sign In",
    busy: "Signing in...",
  },
  signup: {
    intro:
      "Create your account to get a private writing studio: your drafts, examiner feedback, and Vault are visible only to you.",
    submit: "Create Account",
    busy: "Creating account...",
  },
  forgot: {
    intro: "Enter your account email and we will send you a link to choose a new password.",
    submit: "Send Reset Link",
    busy: "Sending...",
  },
  reset: {
    intro: "Choose a new password for your account.",
    submit: "Save New Password",
    busy: "Saving...",
  },
};

/** Human-readable messages for `?error=...` redirects back to `/login`. */
const LOGIN_LINK_ERRORS: Record<string, string> = {
  google_sign_in_failed: "Google sign-in could not be completed. Please try again.",
  google_email_not_verified:
    "Your Google account email is not verified. Verify it with Google or sign in with email and password.",
  signup_not_allowed: "This email address is not permitted to create an account.",
  google_account_not_linked:
    "An account with this email already exists. Sign in with your email and password.",
  invalid_token: "This verification link is invalid or has expired.",
};

function GoogleIcon() {
  return (
    <svg className="w-4 h-4 shrink-0" viewBox="0 0 24 24" aria-hidden="true">
      <path
        fill="#4285F4"
        d="M23.49 12.27c0-.79-.07-1.55-.2-2.27H12v4.3h6.44a5.51 5.51 0 0 1-2.39 3.61v3h3.87c2.26-2.08 3.57-5.15 3.57-8.64Z"
      />
      <path
        fill="#34A853"
        d="M12 24c3.24 0 5.95-1.07 7.93-2.91l-3.87-3c-1.07.72-2.45 1.15-4.06 1.15-3.12 0-5.77-2.11-6.72-4.95H1.28v3.09A12 12 0 0 0 12 24Z"
      />
      <path
        fill="#FBBC05"
        d="M5.28 14.29a7.21 7.21 0 0 1 0-4.58V6.62H1.28a12 12 0 0 0 0 10.76l4-3.09Z"
      />
      <path
        fill="#EA4335"
        d="M12 4.76c1.76 0 3.34.61 4.58 1.8l3.44-3.44C17.95 1.19 15.24 0 12 0A12 12 0 0 0 1.28 6.62l4 3.09c.95-2.84 3.6-4.95 6.72-4.95Z"
      />
    </svg>
  );
}

/** Only allow same-site relative redirects after sign-in. */
function safeNextPath(raw: string | null): string {
  if (!raw || !raw.startsWith("/") || raw.startsWith("//") || raw.startsWith("/\\")) {
    return "/";
  }
  return raw;
}

const inputClass =
  "w-full px-4 py-3 rounded-lg bg-canvas border border-line-strong text-ink placeholder:text-ink-muted focus:outline-none focus:border-accent transition";
const labelClass =
  "block text-xs font-semibold uppercase tracking-wider text-ink-secondary mb-2";

export function AuthForm({
  mode,
  googleEnabled = false,
  passwordResetEnabled = true,
}: {
  mode: AuthMode;
  googleEnabled?: boolean;
  /** False when the deployment cannot deliver reset emails. */
  passwordResetEnabled?: boolean;
}) {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const nextPath = safeNextPath(searchParams.get("next"));
  const resetToken = searchParams.get("token");
  const linkError = searchParams.get("error");

  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(() => {
    if (mode === "reset" && (linkError || !resetToken)) {
      return "This reset link is invalid or has expired. Request a new one.";
    }
    if (mode === "login" && linkError) {
      return LOGIN_LINK_ERRORS[linkError] ?? "Sign-in could not be completed. Please try again.";
    }
    return null;
  });
  const [notice, setNotice] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const copy = COPY[mode];
  const nextQuery = nextPath !== "/" ? `?next=${encodeURIComponent(nextPath)}` : "";

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setNotice(null);
    setLoading(true);

    try {
      if (mode === "login") {
        const res = await auth.signInEmail({ email, password });
        if (res.error) {
          if (res.error.status === 403) {
            // As before, an unverified address gets a fresh link on every sign-in attempt.
            await auth.sendVerificationEmail({ email, callbackURL: nextPath }).catch(() => {});
          }
          setError(
            res.error.status === 403
              ? "Please verify your email first. We have sent you a new verification link."
              : res.error.message || "Incorrect email or password."
          );
          return;
        }
        navigate(nextPath);
        return;
      }

      if (mode === "signup") {
        const res = await auth.signUpEmail({
          name: name.trim(),
          email,
          password,
          callbackURL: nextPath,
        });
        if (res.error) {
          setError(res.error.message || "Could not create your account.");
          return;
        }
        if (res.data.verificationRequired) {
          // Email verification is required before the first sign-in.
          setNotice(`Check ${email} for a link to verify your email and finish signing up.`);
          return;
        }
        navigate(nextPath);
        return;
      }

      if (mode === "forgot") {
        const res = await auth.requestPasswordReset({ email });
        if (res.error) {
          setError(res.error.message || "Could not send the reset link.");
          return;
        }
        setNotice("If an account exists for that email, a reset link is on its way.");
        return;
      }

      const res = await auth.resetPassword({
        newPassword: password,
        token: resetToken || "",
      });
      if (res.error) {
        setError(res.error.message || "Could not reset your password.");
        return;
      }
      navigate("/login?reset=1");
    } catch {
      setError("Network error. Please try again.");
    } finally {
      setLoading(false);
    }
  }

  function handleGoogle() {
    setError(null);
    setLoading(true);
    // The server sends the browser on to Google and, once signed in, back to nextPath.
    window.location.assign(auth.googleSignInUrl(nextPath));
  }

  const showEmail = mode !== "reset";
  const showPassword = mode !== "forgot";
  const resetBlocked = mode === "reset" && (!resetToken || Boolean(linkError));

  return (
    <main className="min-h-screen flex items-center justify-center px-4 py-12 bg-canvas">
      <div className="w-full max-w-md editorial-surface rounded-2xl p-8 sm:p-10">
        <div className="flex items-center gap-3 mb-6">
          <div className="w-10 h-10 rounded-xl bg-accent text-accent-fg flex items-center justify-center">
            <Feather className="w-5 h-5" />
          </div>
          <div>
            <p className="text-xs font-mono uppercase tracking-widest text-ink-muted">
              Academic IELTS Prep
            </p>
            <h1 className="font-serif text-2xl font-semibold tracking-tight text-ink">
              Escribot
            </h1>
          </div>
        </div>

        <p className="text-sm text-ink-secondary leading-relaxed mb-7">{copy.intro}</p>

        {mode === "login" && searchParams.get("reset") === "1" && !error && (
          <p className="text-sm text-accent font-medium mb-5">
            Password updated. Sign in with your new password.
          </p>
        )}

        {mode === "forgot" && !passwordResetEnabled ? (
          <p
            id="password-reset-unavailable"
            role="status"
            className="text-sm p-3 rounded-lg border border-line-strong bg-subtle text-ink"
          >
            Password reset by email is not available on this site yet. Please contact the
            site owner to regain access to your account.
          </p>
        ) : (
          <form onSubmit={handleSubmit} className="space-y-5" id={`auth-${mode}-form`}>
            {mode === "signup" && (
              <div>
                <label htmlFor="auth-name" className={labelClass}>
                  Name
                </label>
                <input
                  id="auth-name"
                  type="text"
                  required
                  autoComplete="name"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  className={inputClass}
                />
              </div>
            )}

            {showEmail && (
              <div>
                <label htmlFor="auth-email" className={labelClass}>
                  Email
                </label>
                <input
                  id="auth-email"
                  type="email"
                  required
                  autoFocus
                  autoComplete="email"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className={inputClass}
                />
              </div>
            )}

            {showPassword && (
              <div>
                <div className="flex items-baseline justify-between">
                  <label htmlFor="auth-password" className={labelClass}>
                    {mode === "reset" ? "New Password" : "Password"}
                  </label>
                  {mode === "login" && passwordResetEnabled && (
                    <Link
                      to="/forgot-password"
                      className="text-xs text-ink-secondary hover:text-accent transition"
                    >
                      Forgot password?
                    </Link>
                  )}
                </div>
                <input
                  id="auth-password"
                  type="password"
                  required
                  minLength={8}
                  autoComplete={mode === "login" ? "current-password" : "new-password"}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className={inputClass}
                />
                {mode !== "login" && (
                  <p className="text-xs text-ink-muted mt-1.5">At least 8 characters.</p>
                )}
              </div>
            )}

            {error && (
              <div
                id="auth-error-banner"
                role="alert"
                className="flex items-start gap-2.5 p-3 rounded-lg text-sm border"
                style={{
                  backgroundColor: "var(--status-danger-soft)",
                  color: "var(--status-danger)",
                  borderColor: "var(--status-danger)",
                }}
              >
                <AlertCircle className="w-4 h-4 shrink-0 mt-0.5" />
                <span>{error}</span>
              </div>
            )}

            {notice && (
              <div
                id="auth-notice-banner"
                role="status"
                className="flex items-start gap-2.5 p-3 rounded-lg text-sm border border-line-strong bg-subtle text-ink"
              >
                <CheckCircle2 className="w-4 h-4 shrink-0 mt-0.5 text-accent" />
                <span>{notice}</span>
              </div>
            )}

            <button
              id="auth-submit-button"
              type="submit"
              disabled={loading || resetBlocked}
              className="w-full flex items-center justify-center gap-2 py-3 px-4 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium text-sm transition disabled:opacity-50 cursor-pointer"
            >
              <span>{loading ? copy.busy : copy.submit}</span>
              <ArrowRight className="w-4 h-4" />
            </button>
          </form>
        )}

        {googleEnabled && (mode === "login" || mode === "signup") && (
          <div className="mt-5">
            <div className="relative flex items-center justify-center mb-4">
              <div className="w-full border-t border-line" />
              <span className="bg-surface px-3 text-xs uppercase tracking-wider text-ink-muted shrink-0">
                or
              </span>
              <div className="w-full border-t border-line" />
            </div>
            <button
              id="auth-google-button"
              type="button"
              onClick={handleGoogle}
              disabled={loading}
              className="w-full flex items-center justify-center gap-2.5 py-3 px-4 rounded-lg border border-line-strong bg-canvas hover:bg-subtle text-ink font-medium text-sm transition disabled:opacity-50 cursor-pointer"
            >
              <GoogleIcon />
              <span>Continue with Google</span>
            </button>
          </div>
        )}

        {mode === "signup" && (
          <p className="text-xs text-ink-muted mt-4 text-center">
            By creating an account you agree to the{" "}
            <Link to="/terms" className="underline hover:text-ink">
              Terms of Service
            </Link>{" "}
            and{" "}
            <Link to="/privacy" className="underline hover:text-ink">
              Privacy Policy
            </Link>
            .
          </p>
        )}

        <p className="text-sm text-ink-secondary mt-6 text-center">
          {mode === "login" ? (
            <>
              New here?{" "}
              <Link to={`/signup${nextQuery}`} className="text-accent hover:underline">
                Create an account
              </Link>
            </>
          ) : (
            <>
              {mode === "signup" ? "Already have an account? " : "Remembered it? "}
              <Link to={`/login${nextQuery}`} className="text-accent hover:underline">
                Sign in
              </Link>
            </>
          )}
        </p>
      </div>
    </main>
  );
}
