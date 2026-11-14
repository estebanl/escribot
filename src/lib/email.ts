/**
 * Minimal transactional email sender (Resend HTTP API).
 * Without RESEND_API_KEY + EMAIL_FROM nothing is sent; in development the
 * message is printed to the server console so links can be copied from there.
 */
export function isEmailConfigured(): boolean {
  return Boolean(process.env.RESEND_API_KEY?.trim() && process.env.EMAIL_FROM?.trim());
}

/**
 * Password reset needs a way to deliver the link: real email in production,
 * or the server console in development.
 */
export function isPasswordResetAvailable(): boolean {
  return isEmailConfigured() || process.env.NODE_ENV !== "production";
}

export async function sendEmail(params: { to: string; subject: string; text: string }) {
  if (!isEmailConfigured()) {
    if (process.env.NODE_ENV === "production") {
      // Never write sign-in or reset links to production logs.
      console.warn(
        `Email "${params.subject}" was not sent: set RESEND_API_KEY and EMAIL_FROM.`
      );
    } else {
      console.info(
        `[email not configured] To: ${params.to}\nSubject: ${params.subject}\n\n${params.text}`
      );
    }
    return;
  }

  const res = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${process.env.RESEND_API_KEY!.trim()}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      from: process.env.EMAIL_FROM!.trim(),
      to: params.to,
      subject: params.subject,
      text: params.text,
    }),
    signal: AbortSignal.timeout(10_000),
  });

  if (!res.ok) {
    throw new Error(`Email delivery failed (${res.status}).`);
  }
}
