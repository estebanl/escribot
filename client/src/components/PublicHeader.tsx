import { Link } from "react-router";
import { Feather } from "lucide-react";

/** Header for signed-out pages (landing, pricing, legal). */
export function PublicHeader() {
  return (
    <header className="border-b border-line bg-surface">
      <div className="max-w-5xl mx-auto px-4 sm:px-6 h-16 flex items-center justify-between gap-3">
        <Link to="/" className="flex items-center gap-2.5 shrink-0">
          <span className="w-9 h-9 rounded-lg bg-accent text-accent-fg flex items-center justify-center">
            <Feather className="w-4 h-4" />
          </span>
          <span className="font-serif text-lg font-semibold text-ink">Escribot</span>
        </Link>
        <nav aria-label="Site" className="flex items-center gap-1 sm:gap-3 text-sm">
          <Link to="/pricing" className="px-2 py-2 text-ink-secondary hover:text-ink">
            Pricing
          </Link>
          <Link to="/login" className="px-2 py-2 text-ink-secondary hover:text-ink">
            Sign in
          </Link>
          <Link
            to="/signup"
            className="hidden sm:inline-flex px-3.5 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium transition"
          >
            Get started
          </Link>
        </nav>
      </div>
    </header>
  );
}
