import { useEffect } from "react";
import { createBrowserRouter, Outlet, ScrollRestoration, useLocation, useMatches } from "react-router";
import * as Admin from "@/pages/Admin";
import * as Analytics from "@/pages/Analytics";
import * as Auth from "@/pages/Auth";
import * as Drills from "@/pages/Drills";
import { NotFoundPage, PageError, RootError } from "@/pages/Errors";
import * as Home from "@/pages/Home";
import * as Pricing from "@/pages/Pricing";
import * as Privacy from "@/pages/Privacy";
import * as Session from "@/pages/Session";
import * as Settings from "@/pages/Settings";
import * as Terms from "@/pages/Terms";
import * as Vault from "@/pages/Vault";
import * as Welcome from "@/pages/Welcome";
import * as Write from "@/pages/Write";

export const DEFAULT_TITLE = "Escribot — Academic IELTS Writing Studio & Exam Simulator";

interface RouteHandle {
  title?: string;
}

function title(text: string): RouteHandle {
  return { title: `${text} — Escribot` };
}

function RootLayout() {
  const { pathname } = useLocation();
  const matches = useMatches();
  const pageTitle = [...matches].reverse().map((m) => (m.handle as RouteHandle | undefined)?.title).find(Boolean);

  useEffect(() => {
    document.title = pageTitle ?? DEFAULT_TITLE;
  }, [pageTitle]);

  return (
    <>
      {/* A new address is a new page: components start from its data. */}
      <Outlet key={pathname} />
      <ScrollRestoration />
    </>
  );
}

export const router = createBrowserRouter([
  {
    element: <RootLayout />,
    errorElement: <RootError />,
    children: [
      {
        errorElement: <PageError />,
        children: [
          { path: "/", caseSensitive: true, loader: Home.loader, element: <Home.HomePage /> },
          { path: "/login", caseSensitive: true, loader: Auth.loader, element: <Auth.AuthPage mode="login" />, handle: title("Sign In") },
          { path: "/signup", caseSensitive: true, loader: Auth.loader, element: <Auth.AuthPage mode="signup" />, handle: title("Create Account") },
          { path: "/forgot-password", caseSensitive: true, loader: Auth.loader, element: <Auth.AuthPage mode="forgot" />, handle: title("Forgot Password") },
          { path: "/reset-password", caseSensitive: true, loader: Auth.loader, element: <Auth.AuthPage mode="reset" />, handle: title("Reset Password") },
          { path: "/welcome", caseSensitive: true, loader: Welcome.loader, element: <Welcome.WelcomePage />, handle: title("Welcome") },
          { path: "/write/:sessionId", caseSensitive: true, loader: Write.loader, element: <Write.WritePage /> },
          { path: "/session/:sessionId", caseSensitive: true, loader: Session.loader, element: <Session.SessionPage /> },
          { path: "/vault", caseSensitive: true, loader: Vault.loader, element: <Vault.VaultPage /> },
          { path: "/drills", caseSensitive: true, loader: Drills.loader, element: <Drills.DrillsPage /> },
          { path: "/analytics", caseSensitive: true, loader: Analytics.loader, element: <Analytics.AnalyticsPage /> },
          { path: "/settings", caseSensitive: true, loader: Settings.loader, element: <Settings.SettingsPage /> },
          { path: "/pricing", caseSensitive: true, loader: Pricing.loader, element: <Pricing.PricingPage />, handle: title("Pricing") },
          { path: "/terms", caseSensitive: true, loader: Terms.loader, element: <Terms.TermsPage />, handle: title("Terms of Service") },
          { path: "/privacy", caseSensitive: true, loader: Privacy.loader, element: <Privacy.PrivacyPage />, handle: title("Privacy Policy") },
          { path: "/admin", caseSensitive: true, loader: Admin.loader, element: <Admin.AdminPage />, handle: title("Admin") },
          { path: "*", element: <NotFoundPage /> },
        ],
      },
    ],
  },
]);
