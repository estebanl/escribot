/** Sends an error caught by an error boundary to /api/errors (best effort). */
export function reportClientError(error: unknown) {
  try {
    const err = error instanceof Error ? error : new Error(String(error));
    const digest =
      typeof error === "object" && error !== null && "digest" in error
        ? String((error as { digest: unknown }).digest)
        : undefined;
    void fetch("/api/errors", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        message: err.message,
        stack: err.stack,
        digest,
        path: window.location.pathname,
      }),
      keepalive: true,
    }).catch(() => {});
  } catch {
    // Reporting must never break the error screen.
  }
}
