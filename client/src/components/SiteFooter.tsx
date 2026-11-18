import { Link } from "react-router";
import { TRADEMARK_NOTICE } from "@/lib/site";

export function SiteFooter() {
  return (
    <footer className="border-t border-line bg-surface">
      <div className="max-w-5xl mx-auto px-4 sm:px-6 py-8 space-y-3">
        <nav aria-label="Footer" className="flex flex-wrap gap-x-5 gap-y-2 text-sm">
          <Link to="/pricing" className="text-ink-secondary hover:text-ink">
            Pricing
          </Link>
          <Link to="/terms" className="text-ink-secondary hover:text-ink">
            Terms of Service
          </Link>
          <Link to="/privacy" className="text-ink-secondary hover:text-ink">
            Privacy Policy
          </Link>
        </nav>
        <p className="text-xs text-ink-muted leading-relaxed max-w-3xl">{TRADEMARK_NOTICE}</p>
      </div>
    </footer>
  );
}
