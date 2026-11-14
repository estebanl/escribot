import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";

export function LegalPage({
  title,
  updated,
  children,
}: {
  title: string;
  updated: string;
  children: React.ReactNode;
}) {
  return (
    <div className="min-h-screen flex flex-col bg-canvas text-ink">
      <PublicHeader />
      <main className="flex-1 max-w-3xl w-full mx-auto px-4 sm:px-6 py-12">
        <h1 className="font-serif text-3xl sm:text-4xl font-semibold">{title}</h1>
        <p className="text-sm text-ink-muted mt-2">Last updated {updated}</p>
        <div className="legal-prose mt-8">{children}</div>
      </main>
      <SiteFooter />
    </div>
  );
}
