import { useEffect } from "react";
import { isRouteErrorResponse, Link, useRevalidator, useRouteError } from "react-router";
import { reportClientError } from "@/lib/report-client-error";

/** The not-found page, as Next.js showed it, for unknown addresses and data the viewer may not see. */
export function NotFoundPage() {
  useEffect(() => {
    document.title = "404: This page could not be found.";
  }, []);

  return (
    <main className="min-h-screen flex items-center justify-center px-4 bg-canvas text-ink">
      <div className="flex items-center">
        <h1 className="inline-block mr-5 pr-6 text-2xl font-medium leading-[49px] border-r border-line-strong">
          404
        </h1>
        <h2 className="text-sm font-normal leading-[49px]">This page could not be found.</h2>
      </div>
    </main>
  );
}

/** Errors of one page (the equivalent of app/error.tsx). A 404 from a loader is the not-found page. */
export function PageError() {
  const error = useRouteError();
  const notFound = isRouteErrorResponse(error) && error.status === 404;
  const { revalidate } = useRevalidator();

  useEffect(() => {
    if (!notFound) reportClientError(errorOf(error));
  }, [error, notFound]);

  if (notFound) return <NotFoundPage />;

  return (
    <main className="min-h-[60vh] flex items-center justify-center px-4 py-16">
      <div className="max-w-md text-center space-y-4">
        <h1 className="font-serif text-2xl font-semibold text-ink">Something went wrong</h1>
        <p className="text-sm text-ink-secondary">
          The error has been reported. Your saved work is safe; try again, or go back to the
          studio.
        </p>
        <div className="flex gap-3 justify-center">
          <button
            type="button"
            onClick={() => void revalidate()}
            className="px-4 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium cursor-pointer"
          >
            Try again
          </button>
          <Link
            to="/"
            className="px-4 py-2 rounded-lg border border-line-strong text-ink text-sm font-medium"
          >
            Back to studio
          </Link>
        </div>
      </div>
    </main>
  );
}

/** Last-resort boundary for errors in the app's own layout (the equivalent of app/global-error.tsx). */
export function RootError() {
  const error = useRouteError();

  useEffect(() => {
    reportClientError(errorOf(error));
    document.title = "Something went wrong — FluentEscritor";
  }, [error]);

  return (
    <div
      style={{
        fontFamily: "system-ui, sans-serif",
        display: "flex",
        minHeight: "100vh",
        alignItems: "center",
        justifyContent: "center",
        margin: 0,
        padding: 16,
        background: "#fff",
        color: "#000",
      }}
    >
      <div style={{ maxWidth: 420, textAlign: "center" }}>
        <h1 style={{ fontSize: 22 }}>Something went wrong</h1>
        <p style={{ color: "#555", fontSize: 14 }}>The error has been reported. Please try again.</p>
        <button
          type="button"
          onClick={() => window.location.reload()}
          style={{
            padding: "8px 16px",
            borderRadius: 8,
            border: "none",
            background: "#d63b1f",
            color: "#fff",
            cursor: "pointer",
          }}
        >
          Try again
        </button>
      </div>
    </div>
  );
}

/** An error answer from a loader becomes an Error, so the report says what failed. */
function errorOf(error: unknown): unknown {
  if (isRouteErrorResponse(error)) {
    const message =
      typeof error.data === "object" && error.data && "error" in error.data
        ? String((error.data as { error: unknown }).error)
        : error.statusText;
    return new Error(`HTTP ${error.status}${message ? `: ${message}` : ""}`);
  }
  return error;
}
