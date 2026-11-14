import crypto from "crypto";
import { getDb, schema } from "@/lib/db";
import { getOrCreateSettings } from "@/lib/db/scope";
import { decryptString } from "@/lib/crypto";
import type { AIProviderId, NativeLanguageCode } from "@/lib/types";

export interface ResolvedAIConfig {
  userId: string;
  provider: AIProviderId;
  graderModel: string;
  fastModel: string;
  visionModel: string;
  apiKey: string | null;
  allKeys: Partial<Record<AIProviderId, string>>;
  currentBand: number;
  targetBand: number;
  nativeLanguage: NativeLanguageCode;
  l1CoachingEnabled: boolean;
}

export interface ProviderDefaults {
  label: string;
  graderModel: string;
  fastModel: string;
  visionModel: string;
  graderFallbacks?: string[];
  fastFallbacks?: string[];
}

export const PROVIDER_DEFAULTS: Record<AIProviderId, ProviderDefaults> = {
  openrouter: {
    label: "OpenRouter (Sonnet 5.5 + DS Flash Vision)",
    graderModel: "anthropic/claude-sonnet-5.5",
    fastModel: "deepseek/deepseek-v4.1-flash-1017",
    visionModel: "deepseek/deepseek-v4.1-flash-1017",
    graderFallbacks: [
      "anthropic/claude-sonnet-5.5",
      "openai/gpt-6.1-sol",
      "google/gemini-4-argon",
    ],
    fastFallbacks: [
      "deepseek/deepseek-v4.1-flash-1017",
      "anthropic/claude-haiku-5.5",
      "openai/gpt-6-luna",
    ],
  },
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

export async function resolveUserAndAIConfig(userId: string): Promise<ResolvedAIConfig> {
  const db = await getDb();
  const settings = await getOrCreateSettings(db, userId);

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
    openrouter:
      storedKeys.openrouter || process.env.OPENROUTER_API_KEY?.trim() || undefined,
    deepseek: storedKeys.deepseek || process.env.DEEPSEEK_API_KEY?.trim() || undefined,
    gemini: storedKeys.gemini || process.env.GEMINI_API_KEY?.trim() || undefined,
    openai: storedKeys.openai || process.env.OPENAI_API_KEY?.trim() || undefined,
    anthropic:
      storedKeys.anthropic || process.env.ANTHROPIC_API_KEY?.trim() || undefined,
  };

  let provider = ((settings?.activeProvider as AIProviderId) || "openrouter") as AIProviderId;
  // If active provider has no key configured, but openrouter has a key, auto-fallback to openrouter
  if (!allKeys[provider] && allKeys.openrouter) {
    provider = "openrouter";
  }

  const defaults = PROVIDER_DEFAULTS[provider] || PROVIDER_DEFAULTS.openrouter;

  let graderModel = settings?.graderModel || defaults.graderModel;
  let fastModel = settings?.fastModel || defaults.fastModel;
  let visionModel = settings?.visionModel || defaults.visionModel;

  if (provider !== "openrouter") {
    if (graderModel.includes("/")) graderModel = defaults.graderModel;
    if (fastModel.includes("/")) fastModel = defaults.fastModel;
    if (visionModel.includes("/")) visionModel = defaults.visionModel;
  } else {
    if (!graderModel.includes("/")) graderModel = defaults.graderModel;
    if (!fastModel.includes("/")) fastModel = defaults.fastModel;
    if (!visionModel.includes("/")) visionModel = defaults.visionModel;
  }

  return {
    userId,
    provider,
    graderModel,
    fastModel,
    visionModel,
    apiKey: allKeys[provider] || null,
    allKeys,
    currentBand: settings?.currentBand ?? 6.5,
    targetBand: settings?.targetBand ?? 7.5,
    nativeLanguage: (settings?.nativeLanguage as NativeLanguageCode) || "es",
    l1CoachingEnabled: settings?.l1CoachingEnabled ?? true,
  };
}

export interface GenerateCompletionOptions {
  /** Owner of the request; AI usage and spend are recorded against this user. */
  userId: string;
  provider: AIProviderId;
  apiKey: string;
  model: string;
  fallbackModels?: string[];
  feature?: string;
  systemPrompt: string;
  userPrompt: string;
  imageDataUrl?: string;
  jsonMode?: boolean;
  thinkingMode?: "enabled" | "disabled";
  reasoningEffort?: "low" | "high" | "max";
  maxTokens?: number;
  timeoutMs?: number;
}

export interface LLMResultWithMeta {
  content: string;
  modelUsed: string;
  costUsd?: number;
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
}

function splitDataUrl(dataUrl: string): { mimeType: string; base64: string } | null {
  const match = dataUrl.match(/^data:([^;]+);base64,(.+)$/);
  if (!match) return null;
  return { mimeType: match[1], base64: match[2] };
}

function isAbortOrTimeout(err: unknown): boolean {
  if (!(err instanceof Error)) return false;
  return (
    err.name === "TimeoutError" ||
    err.name === "AbortError" ||
    /timed?\s*out|aborted due to timeout/i.test(err.message)
  );
}

export async function callLLMWithMeta(
  opts: GenerateCompletionOptions
): Promise<LLMResultWithMeta> {
  const {
    userId,
    provider,
    apiKey,
    model,
    fallbackModels,
    feature,
    systemPrompt,
    userPrompt,
    imageDataUrl,
    jsonMode = true,
    thinkingMode = "disabled",
    reasoningEffort = "high",
    maxTokens,
    timeoutMs = 45_000,
  } = opts;

  const signal = AbortSignal.timeout(timeoutMs);

  try {
    /* -------------------- 0. OPENROUTER (Unified Multi-Provider API) -------------------- */
    if (provider === "openrouter") {
      const baseUrl = (
        process.env.OPENROUTER_BASE_URL?.trim() || "https://openrouter.ai/api/v1"
      ).replace(/\/+$/, "");

      const userContent = imageDataUrl
        ? [
            { type: "text", text: userPrompt },
            {
              type: "image_url",
              image_url: { url: imageDataUrl },
            },
          ]
        : userPrompt;

      const modelList = Array.from(
        new Set([model, ...(fallbackModels || [])].filter(Boolean))
      );

      const body: Record<string, unknown> = {
        model: modelList[0],
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userContent },
        ],
        provider: {
          data_collection: "deny",
        },
        temperature: 0.25,
      };

      if (thinkingMode === "enabled") {
        body.reasoning = { effort: reasoningEffort };
      }

      if (modelList.length > 1) {
        body.models = modelList;
      }

      if (typeof maxTokens === "number" && maxTokens > 0) {
        // OpenRouter models (especially reasoning models like DeepSeek, Claude 3.7+, o-series)
        // allocate internal reasoning tokens against max_tokens before producing content.
        // Buffer max_tokens so reasoning does not exhaust the token window and truncate output.
        body.max_tokens = Math.max(maxTokens + 2048, 4096);
      }

      if (jsonMode) {
        body.response_format = { type: "json_object" };
      }

      const res = await fetch(`${baseUrl}/chat/completions`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${apiKey}`,
          "HTTP-Referer": "https://fluentescritor.app",
          "X-Title": "FluentEscritor",
        },
        body: JSON.stringify(body),
        signal,
      });

      if (!res.ok) {
        const errText = await res.text();
        throw new Error(
          `OpenRouter API error (${res.status}): ${errText.slice(0, 300)}`
        );
      }

      const data = (await res.json()) as {
        id?: string;
        model?: string;
        choices?: Array<{
          message?: {
            content?: string;
            reasoning?: string;
            refusal?: string;
          };
          finish_reason?: string;
        }>;
        usage?: {
          prompt_tokens?: number;
          completion_tokens?: number;
          total_tokens?: number;
          cost?: number;
        };
      };

      let content = data.choices?.[0]?.message?.content;
      if (!content) {
        const msg = data.choices?.[0]?.message;
        if (msg?.refusal) {
          throw new Error(`OpenRouter model refused request: ${msg.refusal}`);
        }
        if (msg?.reasoning && !msg.content) {
          content = msg.reasoning;
        }
      }

      if (!content) {
        const finishReason = data.choices?.[0]?.finish_reason || "unknown";
        throw new Error(
          `Empty response from OpenRouter API (model: ${data.model || model}, finish_reason: ${finishReason}).`
        );
      }

      const actualModel = data.model || model;
      const costUsd = typeof data.usage?.cost === "number" ? data.usage.cost : undefined;

      // Await usage logging so serverless / edge environments persist spend before execution ends
      try {
        const database = await getDb();
        await database.insert(schema.aiUsage).values({
          id: `use-${crypto.randomUUID().slice(0, 10)}`,
          userId,
          feature: feature || "general",
          provider: "openrouter",
          modelUsed: actualModel,
          promptTokens: data.usage?.prompt_tokens ?? null,
          completionTokens: data.usage?.completion_tokens ?? null,
          totalTokens: data.usage?.total_tokens ?? null,
          costUsd: costUsd ?? null,
          createdAt: new Date().toISOString(),
        });
      } catch (logErr) {
        console.warn("Failed to record OpenRouter AI usage in database:", logErr);
      }

      return {
        content,
        modelUsed: actualModel,
        costUsd,
        promptTokens: data.usage?.prompt_tokens,
        completionTokens: data.usage?.completion_tokens,
        totalTokens: data.usage?.total_tokens,
      };
    }

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
        body.temperature = 0.25;
      }

      if (typeof maxTokens === "number" && maxTokens > 0) {
        body.max_tokens = maxTokens;
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
        usage?: {
          prompt_tokens?: number;
          completion_tokens?: number;
          total_tokens?: number;
        };
      };
      const content = data.choices?.[0]?.message?.content;
      if (!content) throw new Error("Empty response from DeepSeek API.");
      return {
        content,
        modelUsed: effectiveModel,
        promptTokens: data.usage?.prompt_tokens,
        completionTokens: data.usage?.completion_tokens,
        totalTokens: data.usage?.total_tokens,
      };
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

      if (typeof maxTokens === "number" && maxTokens > 0) {
        body.max_tokens = maxTokens;
      }

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
        usage?: {
          prompt_tokens?: number;
          completion_tokens?: number;
          total_tokens?: number;
        };
      };
      const content = data.choices?.[0]?.message?.content;
      if (!content) throw new Error("Empty response from OpenAI API.");
      return {
        content,
        modelUsed: model,
        promptTokens: data.usage?.prompt_tokens,
        completionTokens: data.usage?.completion_tokens,
        totalTokens: data.usage?.total_tokens,
      };
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

      const generationConfig: Record<string, unknown> = jsonMode
        ? { responseMimeType: "application/json", temperature: 0.25 }
        : { temperature: 0.3 };

      if (typeof maxTokens === "number" && maxTokens > 0) {
        generationConfig.maxOutputTokens = maxTokens;
      }

      const body: Record<string, unknown> = {
        systemInstruction: { parts: [{ text: systemPrompt }] },
        contents: [{ role: "user", parts }],
        generationConfig,
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
      return { content: text, modelUsed: model };
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
          max_tokens: maxTokens ?? 4096,
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
        usage?: {
          input_tokens?: number;
          output_tokens?: number;
        };
      };
      const text = data.content
        ?.filter((c) => c.type === "text")
        ?.map((c) => c.text || "")
        .join("");
      if (!text) throw new Error("Empty response from Anthropic API.");
      return {
        content: text,
        modelUsed: model,
        promptTokens: data.usage?.input_tokens,
        completionTokens: data.usage?.output_tokens,
        totalTokens:
          data.usage?.input_tokens && data.usage?.output_tokens
            ? data.usage.input_tokens + data.usage.output_tokens
            : undefined,
      };
    }

    throw new Error(`Unsupported AI provider: ${provider}`);
  } catch (err) {
    if (isAbortOrTimeout(err)) {
      const timeoutError = new Error(
        `AI model (${model}) timed out after ${Math.round(timeoutMs / 1000)}s.`
      );
      timeoutError.name = "TimeoutError";
      throw timeoutError;
    }
    throw err;
  }
}

export async function callLLM(opts: GenerateCompletionOptions): Promise<string> {
  const result = await callLLMWithMeta(opts);
  return result.content;
}
