import { Suspense } from "react";
import { redirect } from "next/navigation";
import { AuthForm } from "@/components/AuthForm";
import { isGoogleSignInEnabled } from "@/lib/auth";
import { getCurrentUser } from "@/lib/session";

export const dynamic = "force-dynamic";

export const metadata = { title: "Forgot Password — FluentEscritor" };

export default async function ForgotPasswordPage() {
  if (await getCurrentUser()) redirect("/");

  return (
    <Suspense>
      <AuthForm mode="forgot" googleEnabled={isGoogleSignInEnabled()} />
    </Suspense>
  );
}
