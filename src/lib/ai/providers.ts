import { eq } from "drizzle-orm";
import { getDb, schema } from "@/lib/db";
import { decryptString } from "@/lib/crypto";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

export interface ResolvedAIConfig {
  provider: AIProviderId;
  graderModel: string;
  fastModel: string;
  apiKey: string | null;
  allKeys: Partial<Record<AIProviderId, string>>;
  currentBand: number;
  targetBand: number;
  nativeLanguage: NativeLanguageCode;
  l1CoachingEnabled: boolean;
}

export const PROVIDER_DEFAULTS: Record<
  AIProviderId,
  { label: string; graderModel: string; fastModel: string; visionModel: string }
> = {
  deepseek: {
    label: "DeepSeek (V4 Pro + V4.1 Flash Vision)",
    graderModel: "deepseek-v4-pro",
    fastModel: "deepseek-flash",
    visionModel: "deepseek-flash",
  },
  gemini: {
    label: "Google Gemini (2.5 Pro + 2.5 Flash)",
    graderModel: "gemini-2.5-pro",
    fastModel: "gemini-2.5-flash",
    visionModel: "gemini-2.5-flash",
  },
  openai: {
    label: "OpenAI (GPT-4.1 + GPT-4.1 Mini)",
    graderModel: "gpt-4.1",
    fastModel: "gpt-4.1-mini",
    visionModel: "gpt-4.1",
  },
  anthropic: {
    label: "Anthropic Claude (Sonnet 4.5 + Haiku 4.5)",
    graderModel: "claude-sonnet-4-5",
    fastModel: "claude-haiku-4-5",
    visionModel: "claude-sonnet-4-5",
  },
};

export async function resolveUserAndAIConfig(): Promise<ResolvedAIConfig> {
  const db = await getDb();
  const [settings] = await db
    .select()
    .from(schema.userSettings)
    .where(eq(schema.userSettings.id, 1));

  let storedKeys: Partial<Record<AIProviderId, string>> = {};
  if (settings?.encryptedApiKeys && settings.encryptedApiKeys !== "{}") {
    try {
      const parsed = JSON.parse(settings.encryptedApiKeys) as Record<string, string>;
      for (const [prov, enc] of Object.entries(parsed)) {
        if (enc) {
          const dec = decryptString(enc);
          if (dec) storedKeys[prov as AIProviderId] = dec;
        }
      }
    } catch {
      storedKeys = {};
    }
  }

  const allKeys: Partial<Record<AIProviderId, string>> = {
    deepseek: storedKeys.deepseek || process.env.DEEPSEEK_API_KEY?.trim() || undefined,
    gemini: storedKeys.gemini || process.env.GEMINI_API_KEY?.trim() || undefined,
    openai: storedKeys.openai || process.env.OPENAI_API_KEY?.trim() || undefined,
    anthropic:
      storedKeys.anthropic || process.env.ANTHROPIC_API_KEY?.trim() || undefined,
  };

  const provider = ((settings?.activeProvider as AIProviderId) || "deepseek") as AIProviderId;
  const defaults = PROVIDER_DEFAULTS[provider] || PROVIDER_DEFAULTS.deepseek;

  return {
    provider,
    graderModel: settings?.graderModel || defaults.graderModel,
    fastModel: settings?.fastModel || defaults.fastModel,
    apiKey: allKeys[provider] || null,
    allKeys,
    currentBand: settings?.currentBand ?? 6.5,
    targetBand: settings?.targetBand ?? 7.5,
    nativeLanguage: (settings?.nativeLanguage as NativeLanguageCode) || "es",
    l1CoachingEnabled: settings?.l1CoachingEnabled ?? true,
  };
}

export interface GenerateCompletionOptions {
  provider: AIProviderId;
  apiKey: string;
  model: string;
  systemPrompt: string;
  userPrompt: string;
  imageDataUrl?: string;
  jsonMode?: boolean;
  thinkingMode?: "enabled" | "disabled";
  reasoningEffort?: "low" | "high" | "max";
  timeoutMs?: number;
}

function splitDataUrl(dataUrl: string): { mimeType: string; base64: string } | null {
  const match = dataUrl.match(/^data:([^;]+);base64,(.+)$/);
  if (!match) return null;
  return { mimeType: match[1], base64: match[2] };
}

export async function callLLM(opts: GenerateCompletionOptions): Promise<string> {
  const {
    provider,
    apiKey,
    model,
    systemPrompt,
    userPrompt,
    imageDataUrl,
    jsonMode = true,
    thinkingMode = "disabled",
    reasoningEffort = "high",
    timeoutMs = 95_000,
  } = opts;

  const signal = AbortSignal.timeout(timeoutMs);

  /* -------------------- 1. DEEPSEEK (OpenAI-compatible v4/v4.1) -------------------- */
  if (provider === "deepseek") {
    const baseUrl = (
      process.env.DEEPSEEK_BASE_URL?.trim() || "https://api.deepseek.com"
    ).replace(/\/+$/, "");

    // DeepSeek-V4-Pro does not accept image blocks; ensure deepseek-flash is used if image is attached
    const effectiveModel =
      imageDataUrl && model.includes("pro") ? "deepseek-flash" : model;

    const userContent = imageDataUrl
      ? [
          { type: "text", text: userPrompt },
          {
            type: "image_url",
            image_url: { url: imageDataUrl, detail: "high" },
          },
        ]
      : userPrompt;

    const body: Record<string, unknown> = {
      model: effectiveModel,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: userContent },
      ],
      thinking: { type: thinkingMode },
    };

    if (thinkingMode === "enabled") {
      body.reasoning_effort = reasoningEffort;
    } else {
      body.temperature = 0.3;
    }

    if (jsonMode) {
      body.response_format = { type: "json_object" };
    }

    const res = await fetch(`${baseUrl}/chat/completions`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${apiKey}`,
      },
      body: JSON.stringify(body),
      signal,
    });

    if (!res.ok) {
      const errText = await res.text();
      throw new Error(`DeepSeek API error (${res.status}): ${errText.slice(0, 300)}`);
    }

    const data = (await res.json()) as {
      choices?: Array<{ message?: { content?: string } }>;
    };
    const content = data.choices?.[0]?.message?.content;
    if (!content) throw new Error("Empty response from DeepSeek API.");
    return content;
  }

  /* -------------------- 2. OPENAI -------------------- */
  if (provider === "openai") {
    const userContent = imageDataUrl
      ? [
          { type: "text", text: userPrompt },
          { type: "image_url", image_url: { url: imageDataUrl, detail: "high" } },
        ]
      : userPrompt;

    const body: Record<string, unknown> = {
      model,
      messages: [
        { role: "system", content: systemPrompt },
        { role: "user", content: userContent },
      ],
    };

    if (jsonMode) {
      body.response_format = { type: "json_object" };
    }

    const res = await fetch("https://api.openai.com/v1/chat/completions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${apiKey}`,
      },
      body: JSON.stringify(body),
      signal,
    });

    if (!res.ok) {
      const errText = await res.text();
      throw new Error(`OpenAI API error (${res.status}): ${errText.slice(0, 300)}`);
    }

    const data = (await res.json()) as {
      choices?: Array<{ message?: { content?: string } }>;
    };
    const content = data.choices?.[0]?.message?.content;
    if (!content) throw new Error("Empty response from OpenAI API.");
    return content;
  }

  /* -------------------- 3. GOOGLE GEMINI -------------------- */
  if (provider === "gemini") {
    const parts: Array<Record<string, unknown>> = [{ text: userPrompt }];
    if (imageDataUrl) {
      const parsed = splitDataUrl(imageDataUrl);
      if (parsed) {
        parts.push({
          inlineData: {
            mimeType: parsed.mimeType,
            data: parsed.base64,
          },
        });
      }
    }

    const body: Record<string, unknown> = {
      systemInstruction: { parts: [{ text: systemPrompt }] },
      contents: [{ role: "user", parts }],
      generationConfig: jsonMode
        ? { responseMimeType: "application/json", temperature: 0.25 }
        : { temperature: 0.3 },
    };

    const url = `https://generativelanguage.googleapis.com/v1beta/models/${encodeURIComponent(
      model
    )}:generateContent?key=${encodeURIComponent(apiKey)}`;

    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });

    if (!res.ok) {
      const errText = await res.text();
      throw new Error(`Gemini API error (${res.status}): ${errText.slice(0, 300)}`);
    }

    const data = (await res.json()) as {
      candidates?: Array<{ content?: { parts?: Array<{ text?: string }> } }>;
    };
    const text = data.candidates?.[0]?.content?.parts
      ?.map((p) => p.text || "")
      .join("");
    if (!text) throw new Error("Empty response from Gemini API.");
    return text;
  }

  /* -------------------- 4. ANTHROPIC CLAUDE -------------------- */
  if (provider === "anthropic") {
    const contentBlocks: Array<Record<string, unknown>> = [];
    if (imageDataUrl) {
      const parsed = splitDataUrl(imageDataUrl);
      if (parsed) {
        contentBlocks.push({
          type: "image",
          source: {
            type: "base64",
            media_type: parsed.mimeType,
            data: parsed.base64,
          },
        });
      }
    }
    contentBlocks.push({
      type: "text",
      text: jsonMode
        ? `${userPrompt}\n\nRespond strictly with a single valid JSON object and no markdown prose outside JSON.`
        : userPrompt,
    });

    const res = await fetch("https://api.anthropic.com/v1/messages", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "x-api-key": apiKey,
        "anthropic-version": "2023-06-01",
      },
      body: JSON.stringify({
        model,
        max_tokens: 4096,
        system: systemPrompt,
        messages: [{ role: "user", content: contentBlocks }],
      }),
      signal,
    });

    if (!res.ok) {
      const errText = await res.text();
      throw new Error(`Anthropic API error (${res.status}): ${errText.slice(0, 300)}`);
    }

    const data = (await res.json()) as {
      content?: Array<{ type: string; text?: string }>;
    };
    const text = data.content
      ?.filter((c) => c.type === "text")
      .map((c) => c.text || "")
      .join("");
    if (!text) throw new Error("Empty response from Anthropic API.");
    return text;
  }

  throw new Error(`Unsupported AI provider: ${provider}`);
}
