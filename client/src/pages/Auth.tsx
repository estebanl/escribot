import { useLoaderData } from "react-router";
import { AuthForm, type AuthMode } from "@/components/AuthForm";
import { loadJson, redirectIfSignedIn } from "@/lib/api";

interface AuthConfig {
  googleEnabled: boolean;
  passwordResetAvailable: boolean;
}

/** The sign-in pages: someone already signed in goes to the studio. */
export async function loader({ request }: { request: Request }) {
  const [, config] = await Promise.all([
    redirectIfSignedIn(request),
    loadJson<AuthConfig>("/api/auth/config", request),
  ]);
  return config;
}

export function AuthPage({ mode }: { mode: AuthMode }) {
  const config = useLoaderData<typeof loader>();
  return (
    <AuthForm
      mode={mode}
      googleEnabled={config.googleEnabled}
      // Only the pages that offer a reset link need to know; the others keep the default.
      {...(mode === "login" || mode === "forgot"
        ? { passwordResetEnabled: config.passwordResetAvailable }
        : {})}
    />
  );
}
