import { Suspense } from "react";
import { redirect } from "next/navigation";
import { AuthForm } from "@/components/AuthForm";
import { isGoogleSignInEnabled } from "@/lib/auth";
import { getCurrentUser } from "@/lib/session";

export const dynamic = "force-dynamic";

export const metadata = { title: "Sign In — FluentEscritor" };

export default async function LoginPage() {
  if (await getCurrentUser()) redirect("/");

  return (
    <Suspense>
      <AuthForm mode="login" googleEnabled={isGoogleSignInEnabled()} />
    </Suspense>
  );
}
