import { redirect } from "next/navigation";
import { requireUser } from "@/lib/session";
import { getDb } from "@/lib/db";
import { getOrCreateSettings } from "@/lib/db/scope";
import { WelcomeClient } from "@/components/WelcomeClient";

export const dynamic = "force-dynamic";

export const metadata = { title: "Welcome — FluentEscritor" };

export default async function WelcomePage() {
  const user = await requireUser();
  const settings = await getOrCreateSettings(await getDb(), user.id);
  if (settings.onboardedAt) redirect("/");

  return <WelcomeClient firstName={user.name.trim().split(/\s+/)[0] ?? ""} />;
}
