import { NextResponse } from "next/server";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { getOrCreateSettings } from "@/lib/db/scope";
import { getCurrentUser, unauthorized } from "@/lib/session";
import { decryptString, encryptString, maskApiKey } from "@/lib/crypto";
import { PROVIDER_DEFAULTS } from "@/lib/ai/providers";
import { getUsageSummary } from "@/lib/ai/access";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

export async function GET() {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const db = await getDb();
    const row = await getOrCreateSettings(db, user.id);

    let rawEncryptedMap: Record<string, string> = {};
    try {
      rawEncryptedMap = JSON.parse(row?.encryptedApiKeys || "{}");
    } catch {
      rawEncryptedMap = {};
    }

    const providers: AIProviderId[] = [
      "openrouter",
      "deepseek",
      "gemini",
      "openai",
      "anthropic",
    ];
    const keyStatus: Record<
      AIProviderId,
      { hasStoredKey: boolean; maskedStoredKey: string | null; hasEnvKey: boolean }
    > = {
      openrouter: {
        hasStoredKey: false,
        maskedStoredKey: null,
        hasEnvKey: Boolean(process.env.OPENROUTER_API_KEY?.trim()),
      },
      deepseek: {
        hasStoredKey: false,
        maskedStoredKey: null,
        hasEnvKey: Boolean(process.env.DEEPSEEK_API_KEY?.trim()),
      },
      gemini: {
        hasStoredKey: false,
        maskedStoredKey: null,
        hasEnvKey: Boolean(process.env.GEMINI_API_KEY?.trim()),
      },
      openai: {
        hasStoredKey: false,
        maskedStoredKey: null,
        hasEnvKey: Boolean(process.env.OPENAI_API_KEY?.trim()),
      },
      anthropic: {
        hasStoredKey: false,
        maskedStoredKey: null,
        hasEnvKey: Boolean(process.env.ANTHROPIC_API_KEY?.trim()),
      },
    };

    for (const p of providers) {
      if (rawEncryptedMap[p]) {
        const dec = decryptString(rawEncryptedMap[p]);
        if (dec) {
          keyStatus[p].hasStoredKey = true;
          keyStatus[p].maskedStoredKey = maskApiKey(dec);
        }
      }
    }

    let activeProvider = (row?.activeProvider as AIProviderId) || "openrouter";
    // Migration fallback check
    if (
      !keyStatus[activeProvider]?.hasStoredKey &&
      !keyStatus[activeProvider]?.hasEnvKey &&
      (keyStatus.openrouter.hasStoredKey || keyStatus.openrouter.hasEnvKey)
    ) {
      activeProvider = "openrouter";
    }

    const activeReady =
      keyStatus[activeProvider]?.hasStoredKey || keyStatus[activeProvider]?.hasEnvKey;

    const defaults = PROVIDER_DEFAULTS[activeProvider] || PROVIDER_DEFAULTS.openrouter;

    let graderModel = row?.graderModel || defaults.graderModel;
    let fastModel = row?.fastModel || defaults.fastModel;
    let visionModel = row?.visionModel || defaults.visionModel;

    if (activeProvider !== "openrouter") {
      if (graderModel.includes("/")) graderModel = defaults.graderModel;
      if (fastModel.includes("/")) fastModel = defaults.fastModel;
      if (visionModel.includes("/")) visionModel = defaults.visionModel;
    } else {
      if (!graderModel.includes("/")) graderModel = defaults.graderModel;
      if (!fastModel.includes("/")) fastModel = defaults.fastModel;
      if (!visionModel.includes("/")) visionModel = defaults.visionModel;
    }

    return NextResponse.json({
      settings: {
        currentBand: row?.currentBand ?? 6.5,
        targetBand: row?.targetBand ?? 7.5,
        examDate: row?.examDate ?? "",
        nativeLanguage: (row?.nativeLanguage as NativeLanguageCode) || "es",
        l1CoachingEnabled: row?.l1CoachingEnabled ?? true,
        activeProvider,
        graderModel,
        fastModel,
        visionModel,
      },
      keyStatus,
      activeProviderReady: activeReady,
      providerDefaults: PROVIDER_DEFAULTS,
      usage: await getUsageSummary(user.id),
    });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to load settings." },
      { status: 500 }
    );
  }
}

export async function PUT(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const body = (await request.json()) as {
      currentBand?: number;
      targetBand?: number;
      examDate?: string | null;
      nativeLanguage?: NativeLanguageCode;
      l1CoachingEnabled?: boolean;
      activeProvider?: AIProviderId;
      graderModel?: string;
      fastModel?: string;
      visionModel?: string;
      apiKeysToUpdate?: Partial<Record<AIProviderId, string | null>>;
    };

    const db = await getDb();
    const existing = await getOrCreateSettings(db, user.id);

    let encryptedMap: Record<string, string> = {};
    try {
      encryptedMap = JSON.parse(existing?.encryptedApiKeys || "{}");
    } catch {
      encryptedMap = {};
    }

    // Re-encrypt keys saved under an older secret (see decryptString) so they
    // end up protected by the current APP_SECRET.
    for (const [prov, enc] of Object.entries(encryptedMap)) {
      const plain = decryptString(enc);
      if (plain) encryptedMap[prov] = encryptString(plain);
    }

    if (body.apiKeysToUpdate) {
      for (const [prov, rawVal] of Object.entries(body.apiKeysToUpdate)) {
        if (rawVal === null || rawVal === "__CLEAR__") {
          delete encryptedMap[prov];
        } else if (typeof rawVal === "string" && rawVal.trim().length > 0) {
          encryptedMap[prov] = encryptString(rawVal.trim());
        }
      }
    }

    const activeProvider =
      body.activeProvider || (existing?.activeProvider as AIProviderId) || "openrouter";
    const defaults = PROVIDER_DEFAULTS[activeProvider] || PROVIDER_DEFAULTS.openrouter;

    let graderModel = body.graderModel?.trim() || defaults.graderModel;
    let fastModel = body.fastModel?.trim() || defaults.fastModel;
    let visionModel = body.visionModel?.trim() || defaults.visionModel;

    if (activeProvider !== "openrouter") {
      if (graderModel.includes("/")) graderModel = defaults.graderModel;
      if (fastModel.includes("/")) fastModel = defaults.fastModel;
      if (visionModel.includes("/")) visionModel = defaults.visionModel;
    } else {
      if (!graderModel.includes("/")) graderModel = defaults.graderModel;
      if (!fastModel.includes("/")) fastModel = defaults.fastModel;
      if (!visionModel.includes("/")) visionModel = defaults.visionModel;
    }

    await db
      .update(schema.userSettings)
      .set({
        currentBand:
          typeof body.currentBand === "number"
            ? body.currentBand
            : existing?.currentBand ?? 6.5,
        targetBand:
          typeof body.targetBand === "number"
            ? body.targetBand
            : existing?.targetBand ?? 7.5,
        examDate:
          body.examDate !== undefined ? body.examDate || null : existing?.examDate ?? null,
        nativeLanguage: body.nativeLanguage || existing?.nativeLanguage || "es",
        l1CoachingEnabled:
          typeof body.l1CoachingEnabled === "boolean"
            ? body.l1CoachingEnabled
            : existing?.l1CoachingEnabled ?? true,
        activeProvider,
        graderModel,
        fastModel,
        visionModel,
        encryptedApiKeys: JSON.stringify(encryptedMap),
        updatedAt: new Date().toISOString(),
      })
      .where(eq(schema.userSettings.userId, user.id));

    return NextResponse.json({ ok: true });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to save settings." },
      { status: 500 }
    );
  }
}
