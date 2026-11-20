/**
 * Calls to the .NET server's sign-in endpoints (/api/auth/...). Every call resolves to
 * `{ data }` or `{ error }` and never throws for an
 * error answer; a network failure still rejects.
 */

export interface AuthError {
  status: number;
  /** Machine-readable reason, such as INVALID_PASSWORD or EMAIL_NOT_VERIFIED. */
  code?: string;
  message?: string;
}

export type AuthResult<T> = { data: T; error?: undefined } | { data?: undefined; error: AuthError };

async function post<T>(path: string, body: unknown): Promise<AuthResult<T>> {
  const res = await fetch(`/api/auth/${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const json = await res.json().catch(() => null);
  if (!res.ok) {
    return {
      error: {
        status: res.status,
        code: typeof json?.code === "string" ? json.code : undefined,
        message: typeof json?.error === "string" ? json.error : undefined,
      },
    };
  }
  return { data: json as T };
}

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  emailVerified: boolean;
}

export const auth = {
  signInEmail: (input: { email: string; password: string }) =>
    post<{ user: AuthUser }>("sign-in/email", input),

  /** `verificationRequired` means no session was started: the address must be verified first. */
  signUpEmail: (input: { name: string; email: string; password: string; callbackURL: string }) =>
    post<{ user: AuthUser | null; verificationRequired: boolean }>("sign-up/email", input),

  sendVerificationEmail: (input: { email: string; callbackURL: string }) =>
    post<{ success: boolean }>("send-verification-email", input),

  requestPasswordReset: (input: { email: string }) =>
    post<{ success: boolean }>("request-password-reset", input),

  resetPassword: (input: { newPassword: string; token: string }) =>
    post<{ success: boolean }>("reset-password", input),

  signOut: () => post<{ success: boolean }>("sign-out", {}),

  deleteUser: (input: { password?: string }) => post<{ success: boolean }>("delete-user", input),

  /** Google sign-in is a full-page redirect through the server, which returns to `callbackURL`. */
  googleSignInUrl: (callbackURL: string) =>
    `/api/auth/sign-in/google?callbackURL=${encodeURIComponent(callbackURL)}`,
};
