import { redirect, useLoaderData } from "react-router";
import { WelcomeClient } from "@/components/WelcomeClient";
import { requireUser } from "@/lib/api";

export async function loader({ request }: { request: Request }) {
  const user = await requireUser(request, { allowNotOnboarded: true });
  if (user.onboarded) throw redirect("/");
  return { firstName: user.name.trim().split(/\s+/)[0] ?? "" };
}

export function WelcomePage() {
  const { firstName } = useLoaderData<typeof loader>();
  return <WelcomeClient firstName={firstName} />;
}
