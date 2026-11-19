import type { AIProviderId, ProviderInfo, ProviderModelList } from "@/lib/types";

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

// Lists loaded with the saved (or Escribot's) key, kept for this page view.
const savedKeyModels = new Map<AIProviderId, Promise<ProviderModelList>>();

/**
 * The models a provider offers right now, newest first (POST /api/settings/providers/{id}/models).
 * Pass `apiKey` to list with a key that is not saved yet; without it the saved key, or
 * Escribot's own, is used. OpenRouter's catalogue needs no key.
 */
export function fetchProviderModels(id: AIProviderId, apiKey?: string): Promise<ProviderModelList> {
  const load = async () => {
    const res = await fetch(`/api/settings/providers/${id}/models`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(apiKey ? { apiKey } : {}),
    });
    if (!res.ok) throw await errorFrom(res, "Could not load this provider's models.");
    return (await res.json()) as ProviderModelList;
  };
  if (apiKey) return load();
  let cached = savedKeyModels.get(id);
  if (!cached) {
    cached = load();
    savedKeyModels.set(id, cached);
    cached.catch(() => savedKeyModels.delete(id));
  }
  return cached;
}

/** Forgets lists loaded with a saved key, after that key changes. */
export function forgetProviderModels(id: AIProviderId) {
  savedKeyModels.delete(id);
}

/** The longest model name the API accepts (MaxModelNameLength on the server). */
export const MAX_MODEL_NAME_LENGTH = 200;

/** OpenRouter model names have a slash ("vendor/model"); every other provider's don't. */
export function modelShapeError(id: AIProviderId, model: string): string | null {
  if (model.length > MAX_MODEL_NAME_LENGTH) return `Model names can be up to ${MAX_MODEL_NAME_LENGTH} characters.`;
  const slash = model.indexOf("/");
  if (id === "openrouter" && (slash <= 0 || slash === model.length - 1)) {
    return "OpenRouter models look like vendor/model.";
  }
  if (id !== "openrouter" && slash >= 0) return "Use the provider's own model name, without a vendor/ prefix.";
  return null;
}
