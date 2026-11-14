import { Suspense } from "react";
import { redirect } from "next/navigation";
import { AuthForm } from "@/components/AuthForm";
import { isGoogleSignInEnabled } from "@/lib/auth";
import { getCurrentUser } from "@/lib/session";

export const dynamic = "force-dynamic";

export const metadata = { title: "Reset Password — FluentEscritor" };

export default async function ResetPasswordPage() {
  if (await getCurrentUser()) redirect("/");

  return (
    <Suspense>
      <AuthForm mode="reset" googleEnabled={isGoogleSignInEnabled()} />
    </Suspense>
  );
}
