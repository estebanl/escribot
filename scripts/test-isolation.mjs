#!/usr/bin/env node
/**
 * Tenant-isolation test: proves one account cannot read or change another
 * account's data through any route, and that signed-out requests are refused.
 *
 * Runs against a live deployment or a local API (Node.js 22+, no dependencies):
 *   BASE_URL=https://<your-domain> node scripts/test-isolation.mjs
 * CI runs it against the Docker image through ImageIsolationTests in server/tests.
 *
 * It creates two throwaway accounts, writes data as user A, attacks it as
 * user B, then deletes user B and checks user A is untouched. No AI keys are
 * needed: without one, grading falls back to the local heuristic.
 */
const BASE = (process.env.BASE_URL || "http://localhost:3000").replace(/\/+$/, "");
const run = Date.now().toString(36);
let failures = 0;
let passes = 0;

function check(name, ok, detail = "") {
  if (ok) {
    passes++;
    console.log(`  ✓ ${name}`);
  } else {
    failures++;
    console.log(`  ✗ ${name}${detail ? `  (${detail})` : ""}`);
  }
}

class Client {
  constructor(label) {
    this.label = label;
    this.cookies = new Map();
  }
  async req(method, path, body) {
    const headers = { origin: BASE };
    if (body !== undefined) headers["content-type"] = "application/json";
    if (this.cookies.size) {
      headers.cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join("; ");
    }
    const res = await fetch(BASE + path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      redirect: "manual",
    });
    for (const c of res.headers.getSetCookie?.() ?? []) {
      const [pair] = c.split(";");
      const i = pair.indexOf("=");
      this.cookies.set(pair.slice(0, i).trim(), pair.slice(i + 1).trim());
    }
    const text = await res.text();
    let json = null;
    try {
      json = JSON.parse(text);
    } catch {
      /* not JSON */
    }
    return { status: res.status, text, json, location: res.headers.get("location") };
  }
  get(p) { return this.req("GET", p); }
  post(p, b = {}) { return this.req("POST", p, b); }
  put(p, b = {}) { return this.req("PUT", p, b); }
  patch(p, b = {}) { return this.req("PATCH", p, b); }
  del(p) { return this.req("DELETE", p); }
}

// A tiny valid PNG (1x1 pixel).
const PNG =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

async function signUp(client, who) {
  const email = `isolation-${who}-${run}@example.com`;
  const res = await client.post("/api/auth/sign-up/email", {
    name: `Isolation ${who}`,
    email,
    password: `isolation-password-${run}`,
  });
  if (res.status !== 200 || !client.cookies.size) {
    throw new Error(`Sign-up for ${who} failed: HTTP ${res.status} ${res.text.slice(0, 200)}`);
  }
  return email;
}

async function main() {
  console.log(`Isolation test against ${BASE}\n`);
  const a = new Client("A");
  const b = new Client("B");
  const anon = new Client("anonymous");

  await signUp(a, "a");
  await signUp(b, "b");

  // ---------- user A creates data ----------
  console.log("Setup (user A)");
  const prompt = (
    await a.post("/api/prompts", {
      taskType: "task1",
      promptText: `Isolation prompt ${run}: describe the chart.`,
      title: `Isolation ${run}`,
      imageDataUrl: PNG,
    })
  ).json?.prompt;
  check("A creates a custom prompt with an image", Boolean(prompt?.id && prompt?.imageDataUrl));
  const imageId = prompt?.imageDataUrl?.split("/").pop();

  const session = (await a.post("/api/sessions", { mode: "coach", task1PromptId: prompt?.id })).json
    ?.session;
  check("A creates a writing session", Boolean(session?.id));

  const evalRes = await a.post("/api/ai/evaluate", {
    sessionId: session?.id,
    submissions: [
      {
        promptId: prompt?.id,
        taskType: "task1",
        essayText:
          "The chart illustrates changes over time. Overall, the figures rose steadily across the period shown.",
      },
    ],
  });
  const resultLine = evalRes.text.split("\n").filter(Boolean).map((l) => JSON.parse(l)).find((e) => e.type === "result");
  const draftId = resultLine?.results?.[0]?.draftId;
  check("A gets an essay graded (draft + evaluation)", Boolean(draftId), `HTTP ${evalRes.status}`);

  const vaultItem = (
    await a.post("/api/vault", { originalText: `orig-${run}`, upgradedText: `upgraded-${run}` })
  ).json?.item;
  check("A adds a Vault item", Boolean(vaultItem?.id));

  const drills = (await a.get("/api/drills")).json?.drills ?? [];
  const aSecrets = [prompt?.id, session?.id, draftId, vaultItem?.id, imageId, `orig-${run}`].filter(Boolean);

  // ---------- user B attacks ----------
  console.log("\nUser B cannot read A's data");
  const leaks = (res) => aSecrets.filter((s) => res.text.includes(s));
  for (const path of ["/api/sessions", "/api/prompts", "/api/vault", "/api/drills", "/api/settings", "/api/backup", "/api/account/export"]) {
    const res = await b.get(path);
    check(`GET ${path} contains none of A's data`, res.status === 200 && leaks(res).length === 0, `HTTP ${res.status}, leaked: ${leaks(res).join(", ")}`);
  }
  check("GET /api/sessions/[A's id] → 404", (await b.get(`/api/sessions/${session?.id}`)).status === 404);
  check("GET /api/images/[A's image] → 404", (await b.get(`/api/images/${imageId}`)).status === 404);
  check("Page /session/[A's id] → 404", (await b.get(`/session/${session?.id}`)).status === 404);
  check("Page /write/[A's id] → 404", (await b.get(`/write/${session?.id}`)).status === 404);

  console.log("\nUser B cannot change A's data");
  check("PATCH A's Vault item → 404", (await b.patch("/api/vault", { id: vaultItem?.id, masteryLevel: 3 })).status === 404);
  await b.del(`/api/vault?id=${encodeURIComponent(vaultItem?.id ?? "")}`);
  check("PATCH A's prompt → 404", (await b.patch(`/api/prompts/${prompt?.id}`, { promptText: "hijack" })).status === 404);
  check("DELETE A's prompt → 404", (await b.del(`/api/prompts/${prompt?.id}`)).status === 404);
  check("POST /api/ai/transform-svg on A's prompt → 404 or 403", [403, 404].includes((await b.post("/api/ai/transform-svg", { promptId: prompt?.id })).status));
  check("POST /api/sessions using A's prompt → 404", (await b.post("/api/sessions", { mode: "coach", task1PromptId: prompt?.id })).status === 404);
  check(
    "POST /api/ai/evaluate on A's session → 404",
    (await b.post("/api/ai/evaluate", { sessionId: session?.id, submissions: [{ promptId: prompt?.id, taskType: "task1", essayText: "x".repeat(40) }] })).status === 404
  );
  if (drills[0]) {
    await b.post("/api/drills", { action: "score", drillId: drills[0].id, challenge: {}, userResponse: "hijack" });
  }
  check("Admin API → 404 for a normal user", (await b.patch(`/api/admin/users/${"x"}`, { plan: "pro" })).status === 404);
  check("Admin page → 404 for a normal user", (await b.get("/admin")).status === 404);

  const aVault = (await a.get("/api/vault")).json?.items ?? [];
  const aItem = aVault.find((i) => i.id === vaultItem?.id);
  check("A's Vault item still exists and is unchanged", Boolean(aItem) && aItem.masteryLevel === 0);
  check("A can still load their own image", (await a.get(`/api/images/${imageId}`)).status === 200);
  const aPrompt = ((await a.get("/api/prompts")).json?.prompts ?? []).find((p) => p.id === prompt?.id);
  check("A's prompt still exists and is unchanged", Boolean(aPrompt) && aPrompt.promptText === prompt?.promptText);

  // ---------- signed out ----------
  console.log("\nSigned-out requests are refused");
  for (const [method, path] of [
    ["GET", "/api/sessions"], ["POST", "/api/sessions"], ["GET", `/api/sessions/${session?.id}`],
    ["GET", "/api/prompts"], ["POST", "/api/prompts"], ["PATCH", `/api/prompts/${prompt?.id}`],
    ["DELETE", `/api/prompts/${prompt?.id}`], ["GET", "/api/vault"], ["GET", "/api/drills"],
    ["GET", "/api/settings"], ["PUT", "/api/settings"], ["GET", "/api/backup"], ["POST", "/api/backup"],
    ["GET", "/api/account/export"], ["GET", `/api/images/${imageId}`], ["POST", "/api/ai/evaluate"],
    ["POST", "/api/ai/toolkit"], ["POST", "/api/ai/outline-check"], ["POST", "/api/ai/generate-prompt"],
    ["GET", "/api/ai/models"], ["POST", "/api/billing/checkout"], ["POST", "/api/billing/portal"],
  ]) {
    const res = await anon.req(method, path, method === "GET" ? undefined : {});
    check(`${method} ${path} → 401`, res.status === 401, `HTTP ${res.status}`);
  }
  const page = await anon.get("/vault");
  check("Signed-out page redirects to /login", page.status >= 300 && page.status < 400 && /\/login/.test(page.location ?? ""));

  // ---------- deletion ----------
  console.log("\nDeleting user B leaves user A intact");
  const del = await b.post("/api/auth/delete-user", { password: `isolation-password-${run}` });
  check("B deletes their account", del.status === 200, `HTTP ${del.status} ${del.text.slice(0, 120)}`);
  check("B's session no longer works", (await b.get("/api/settings")).status === 401);
  const aSessions = (await a.get("/api/sessions")).json?.sessions ?? [];
  check("A's sessions are intact", aSessions.some((s) => s.id === session?.id));
  await a.post("/api/auth/delete-user", { password: `isolation-password-${run}` });

  console.log(`\n${passes} passed, ${failures} failed`);
  process.exitCode = failures ? 1 : 0;
}

main().catch((err) => {
  console.error(err);
  process.exitCode = 1;
});
