"use client";

import { useEffect } from "react";
import Link from "next/link";
import { reportClientError } from "@/lib/report-client-error";

export default function Error({ error, retry }: { error: unknown; retry: () => void }) {
  useEffect(() => {
    reportClientError(error);
  }, [error]);

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
            onClick={() => retry()}
            className="px-4 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium cursor-pointer"
          >
            Try again
          </button>
          <Link
            href="/"
            className="px-4 py-2 rounded-lg border border-line-strong text-ink text-sm font-medium"
          >
            Back to studio
          </Link>
        </div>
      </div>
    </main>
  );
}
