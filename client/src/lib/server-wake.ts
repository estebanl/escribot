/**
 * Tells the page when the server is starting up. The API scales to zero after about five
 * idle minutes, and the first request after that waits roughly 30 seconds for it to start,
 * while the page itself loads at once from Cloudflare. Every same-origin /api request goes
 * through `fetch`, so this wraps it: a request still waiting after a few seconds, with no
 * API answer in the last few minutes, means the server is starting rather than working.
 * Once any answer comes back, it is awake.
 */

/** How long a request may wait before the notice shows. */
const SLOW_AFTER_MS = 3_000;

/**
 * After an answer, the server stays up for five idle minutes. Within four of them a slow
 * request is slow work (an essay being graded), not a start.
 */
const AWAKE_FOR_MS = 4 * 60_000;

let lastAnswerAt = Number.NEGATIVE_INFINITY;
let starting = false;
const listeners = new Set<() => void>();

function setStarting(value: boolean) {
  if (starting === value) return;
  starting = value;
  listeners.forEach((listener) => listener());
}

function isApiRequest(input: RequestInfo | URL) {
  try {
    const url = new URL(input instanceof Request ? input.url : String(input), window.location.href);
    return url.origin === window.location.origin && url.pathname.startsWith("/api/");
  } catch {
    return false;
  }
}

/** Wraps `window.fetch` once, before the app makes its first request (see watch-server-wake.ts). */
export function watchServerWake() {
  const original = window.fetch.bind(window);

  window.fetch = async (input, init) => {
    if (!isApiRequest(input)) return original(input, init);

    const timer = window.setTimeout(() => {
      if (Date.now() - lastAnswerAt > AWAKE_FOR_MS) setStarting(true);
    }, SLOW_AFTER_MS);
    try {
      const response = await original(input, init);
      lastAnswerAt = Date.now();
      setStarting(false);
      return response;
    } finally {
      window.clearTimeout(timer);
    }
  };
}

/** For useSyncExternalStore. */
export function subscribeServerWake(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function isServerStarting() {
  return starting;
}
