"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useSyncExternalStore } from "react";
import {
  Feather,
  BookOpen,
  Zap,
  Bookmark,
  LineChart,
  Settings,
  Sun,
  Moon,
  LogOut,
  Sparkles,
} from "lucide-react";
import { authClient } from "@/lib/auth-client";

const NAV_ITEMS = [
  { href: "/", label: "Studio", icon: BookOpen, id: "nav-studio" },
  { href: "/drills", label: "5-Min Drills", icon: Zap, id: "nav-drills" },
  { href: "/vault", label: "Error & Lexical Vault", icon: Bookmark, id: "nav-vault" },
  { href: "/analytics", label: "Band Progress", icon: LineChart, id: "nav-analytics" },
  { href: "/settings", label: "Settings", icon: Settings, id: "nav-settings" },
];

function subscribeTheme(callback: () => void) {
  const observer = new MutationObserver(callback);
  observer.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ["class"],
  });
  return () => observer.disconnect();
}

export function AppShell({
  children,
  user,
}: {
  children: React.ReactNode;
  user?: { name: string; email: string; canUpgrade?: boolean } | null;
}) {
  const pathname = usePathname();
  const router = useRouter();
  const dark = useSyncExternalStore(
    subscribeTheme,
    () => document.documentElement.classList.contains("dark"),
    () => false
  );

  function toggleTheme() {
    const next = !dark;
    document.documentElement.classList.toggle("dark", next);
    try {
      localStorage.setItem("fe-theme", next ? "dark" : "light");
    } catch {
      // ignore storage errors
    }
  }

  async function handleSignOut() {
    await authClient.signOut();
    router.push("/login");
    router.refresh();
  }

  return (
    <div className="min-h-screen flex flex-col bg-canvas text-ink">
      <header className="sticky top-0 z-30 border-b border-line bg-surface/95 backdrop-blur-md">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 h-16 flex items-center justify-between gap-4">
          <Link
            href="/"
            id="brand-home-link"
            className="flex items-center gap-2.5 group shrink-0"
          >
            <div className="w-9 h-9 rounded-lg bg-accent text-accent-fg flex items-center justify-center transition-transform group-hover:scale-105">
              <Feather className="w-4 h-4" />
            </div>
            <div className="leading-tight">
              <span className="font-serif text-lg font-semibold tracking-tight text-ink block">
                FluentEscritor
              </span>
              <span className="text-[10px] font-mono uppercase tracking-widest text-ink-muted hidden sm:block">
                Academic IELTS Studio
              </span>
            </div>
          </Link>

          <nav
            aria-label="Primary Navigation"
            className="hidden lg:flex items-center gap-1"
          >
            {NAV_ITEMS.map((item) => {
              const Icon = item.icon;
              const active =
                item.href === "/"
                  ? pathname === "/"
                  : pathname.startsWith(item.href);
              return (
                <Link
                  key={item.href}
                  id={item.id}
                  href={item.href}
                  className={`flex items-center gap-2 px-2 xl:px-3 h-16 border-b-2 text-sm font-medium whitespace-nowrap transition ${
                    active
                      ? "border-accent text-ink font-semibold"
                      : "border-transparent text-ink-secondary hover:text-ink hover:border-line-strong"
                  }`}
                >
                  <Icon className="w-4 h-4" />
                  <span>{item.label}</span>
                </Link>
              );
            })}
          </nav>

          <div className="flex items-center gap-2">
            <button
              id="theme-toggle-button"
              type="button"
              onClick={toggleTheme}
              title={dark ? "Switch to Inkwell Paper (Light)" : "Switch to Inkwell Night (Dark)"}
              className="p-2 rounded-lg border border-line bg-canvas hover:bg-subtle text-ink-secondary hover:text-ink transition cursor-pointer"
            >
              {dark ? <Sun className="w-4 h-4" /> : <Moon className="w-4 h-4" />}
            </button>

            {user?.canUpgrade && (
              <Link
                id="upgrade-link"
                href="/pricing"
                title="Upgrade to Pro"
                aria-label="Upgrade to Pro"
                className="flex items-center gap-1.5 px-3 py-2 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg text-sm font-medium transition"
              >
                <Sparkles className="w-4 h-4" />
                <span className="hidden xl:inline">Upgrade</span>
              </Link>
            )}

            {user && (
              <button
                id="signout-button"
                type="button"
                onClick={handleSignOut}
                title={`Sign out (${user.email})`}
                className="p-2 rounded-lg border border-line bg-canvas hover:bg-subtle text-ink-secondary hover:text-ink transition cursor-pointer"
              >
                <LogOut className="w-4 h-4" />
              </button>
            )}
          </div>
        </div>
      </header>

      <main className="flex-1 pb-20 lg:pb-10">{children}</main>

      {/* Mobile Companion Bottom Navigation */}
      <nav
        aria-label="Mobile Navigation"
        className="lg:hidden fixed bottom-0 inset-x-0 z-30 border-t border-line bg-surface/95 backdrop-blur-md px-2 py-1.5 flex items-center justify-around"
      >
        {NAV_ITEMS.map((item) => {
          const Icon = item.icon;
          const active =
            item.href === "/" ? pathname === "/" : pathname.startsWith(item.href);
          return (
            <Link
              key={item.href}
              id={`${item.id}-mobile`}
              href={item.href}
              className={`flex flex-col items-center gap-0.5 px-2.5 py-1.5 rounded-lg text-[11px] font-medium transition ${
                active ? "text-accent font-semibold" : "text-ink-muted hover:text-ink"
              }`}
            >
              <Icon className="w-4 h-4" />
              <span>{item.label.split(" ")[0]}</span>
            </Link>
          );
        })}
      </nav>
    </div>
  );
}
