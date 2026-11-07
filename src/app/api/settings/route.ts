import { NextResponse } from "next/server";
import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { decryptString, encryptString, maskApiKey } from "@/lib/crypto";
import { PROVIDER_DEFAULTS } from "@/lib/ai/providers";
import { isPasscodeRequired } from "@/lib/auth";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

export async function GET() {
  try {
    const db = await getDb();
    const [row] = await db
      .select()
      .from(schema.userSettings)
      .where(eq(schema.userSettings.id, 1));

    let rawEncryptedMap: Record<string, string> = {};
    try {
      rawEncryptedMap = JSON.parse(row?.encryptedApiKeys || "{}");
    } catch {
      rawEncryptedMap = {};
    }

    const providers: AIProviderId[] = ["deepseek", "gemini", "openai", "anthropic"];
    const keyStatus: Record<
      AIProviderId,
      { hasStoredKey: boolean; maskedStoredKey: string | null; hasEnvKey: boolean }
    > = {
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

    const activeProvider = (row?.activeProvider as AIProviderId) || "deepseek";
    const activeReady =
      keyStatus[activeProvider]?.hasStoredKey || keyStatus[activeProvider]?.hasEnvKey;

    return NextResponse.json({
      settings: {
        currentBand: row?.currentBand ?? 6.5,
        targetBand: row?.targetBand ?? 7.5,
        examDate: row?.examDate ?? "",
        nativeLanguage: (row?.nativeLanguage as NativeLanguageCode) || "es",
        l1CoachingEnabled: row?.l1CoachingEnabled ?? true,
        activeProvider,
        graderModel:
          row?.graderModel || PROVIDER_DEFAULTS[activeProvider].graderModel,
        fastModel: row?.fastModel || PROVIDER_DEFAULTS[activeProvider].fastModel,
      },
      keyStatus,
      activeProviderReady: activeReady,
      passcodeConfigured: isPasscodeRequired(),
      providerDefaults: PROVIDER_DEFAULTS,
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
    const body = (await request.json()) as {
      currentBand?: number;
      targetBand?: number;
      examDate?: string | null;
      nativeLanguage?: NativeLanguageCode;
      l1CoachingEnabled?: boolean;
      activeProvider?: AIProviderId;
      graderModel?: string;
      fastModel?: string;
      apiKeysToUpdate?: Partial<Record<AIProviderId, string | null>>;
    };

    const db = await getDb();
    const [existing] = await db
      .select()
      .from(schema.userSettings)
      .where(eq(schema.userSettings.id, 1));

    let encryptedMap: Record<string, string> = {};
    try {
      encryptedMap = JSON.parse(existing?.encryptedApiKeys || "{}");
    } catch {
      encryptedMap = {};
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
      body.activeProvider || (existing?.activeProvider as AIProviderId) || "deepseek";
    const defaults = PROVIDER_DEFAULTS[activeProvider] || PROVIDER_DEFAULTS.deepseek;

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
        graderModel: body.graderModel?.trim() || defaults.graderModel,
        fastModel: body.fastModel?.trim() || defaults.fastModel,
        encryptedApiKeys: JSON.stringify(encryptedMap),
        updatedAt: new Date().toISOString(),
      })
      .where(eq(schema.userSettings.id, 1));

    return NextResponse.json({ ok: true });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to save settings." },
      { status: 500 }
    );
  }
}
