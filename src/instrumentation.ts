import type { Instrumentation } from "next";

/**
 * Records uncaught server errors (pages, route handlers, proxy) in the
 * error_events table shown on /admin. Only the Node.js runtime has the
 * database client.
 */
export const onRequestError: Instrumentation.onRequestError = async (err, request, context) => {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { reportError } = await import("@/lib/errors");
  const digest =
    typeof err === "object" && err !== null && "digest" in err
      ? String((err as { digest: unknown }).digest)
      : null;
  await reportError({
    source: "server",
    error: err,
    path: `${request.method} ${request.path} (${context.routeType})`,
    digest,
  });
};
