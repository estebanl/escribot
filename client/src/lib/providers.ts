import type { AIProviderId, ProviderInfo } from "@/lib/types";

async function errorFrom(res: Response, fallback: string): Promise<Error> {
  const body = await res.json().catch(() => null);
  return new Error(body?.error || fallback);
}

/**
 * Adds a provider or changes its key or model list (PUT /api/settings/providers/{id}).
 * Leave `apiKey` out to keep the saved key, and `models` out to keep the list.
 */
export async function saveProvider(
  id: AIProviderId,
  body: { apiKey?: string; models?: string[] }
): Promise<ProviderInfo> {
  const res = await fetch(`/api/settings/providers/${id}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw await errorFrom(res, "Could not save this provider.");
  return (await res.json()) as ProviderInfo;
}

/** Removes a provider's saved key and model list. */
export async function removeProvider(id: AIProviderId): Promise<void> {
  const res = await fetch(`/api/settings/providers/${id}`, { method: "DELETE" });
  if (!res.ok) throw await errorFrom(res, "Could not remove this provider.");
}

/** OpenRouter model names have a slash ("vendor/model"); every other provider's don't. */
export function modelShapeError(id: AIProviderId, model: string): string | null {
  const slashed = model.includes("/");
  if (id === "openrouter" && !slashed) return "OpenRouter models look like vendor/model.";
  if (id !== "openrouter" && slashed) return "Use the provider's own model name, without a vendor/ prefix.";
  return null;
}
