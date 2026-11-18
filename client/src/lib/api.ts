import { data, redirect } from "react-router";
import type { PlanId } from "@/lib/plans";

/** The sign-in page, returning to `target` (path + query) afterwards. */
export function loginUrl(target: string) {
  return target === "/" ? "/login" : `/login?next=${encodeURIComponent(target)}`;
}

function requestTarget(request: Request) {
  const url = new URL(request.url);
  return `${url.pathname}${url.search}`;
}

/**
 * GETs JSON for a route loader. A signed-out answer (401) sends the browser to sign in,
 * and any other error answer becomes the route's error, so a 404 shows the not-found page.
 */
export async function loadJson<T>(path: string, request: Request): Promise<T> {
  const res = await fetch(path, { signal: request.signal, headers: { Accept: "application/json" } });
  if (res.status === 401) {
    throw redirect(loginUrl(requestTarget(request)));
  }
  if (!res.ok) {
    const body = await res.json().catch(() => null);
    throw data(body, { status: res.status });
  }
  return (await res.json()) as T;
}

/** The signed-in person, as GET /api/viewer describes them. */
export interface Viewer {
  id: string;
  name: string;
  email: string;
  emailVerified: boolean;
  hasPassword: boolean;
  plan: PlanId;
  /** Show upgrade prompts: billing is configured and the user is on Free. */
  canUpgrade: boolean;
  isAdmin: boolean;
  /** The welcome questions have been answered. */
  onboarded: boolean;
}

/** The viewer, or null when nobody is signed in. */
export async function getViewer(request: Request): Promise<Viewer | null> {
  const { user } = await loadJson<{ user: Viewer | null }>("/api/viewer", request);
  return user;
}

/**
 * For signed-in pages: the viewer, or a redirect to sign in. Until the welcome questions
 * are answered every signed-in page sends the person to /welcome.
 */
export async function requireUser(
  request: Request,
  { allowNotOnboarded = false }: { allowNotOnboarded?: boolean } = {}
): Promise<Viewer> {
  const user = await getViewer(request);
  if (!user) throw redirect(loginUrl(requestTarget(request)));
  if (!user.onboarded && !allowNotOnboarded) throw redirect("/welcome");
  return user;
}

/** For the admin page: anyone else gets the not-found page, so its existence is not revealed. */
export async function requireAdmin(request: Request): Promise<Viewer> {
  const user = await requireUser(request);
  if (!user.isAdmin) throw notFound();
  return user;
}

/** For the sign-in pages: someone already signed in goes to the studio. */
export async function redirectIfSignedIn(request: Request): Promise<void> {
  if (await getViewer(request)) throw redirect("/");
}

export function notFound() {
  return data(null, { status: 404 });
}
