import { NextResponse } from "next/server";
import { getCurrentUser, unauthorized } from "@/lib/session";

interface OpenRouterModel {
  id: string;
  name?: string;
  context_length?: number;
  architecture?: {
    input_modalities?: string[];
  };
  supported_parameters?: string[];
  pricing?: {
    prompt?: string | number;
    completion?: string | number;
  };
}

interface CachedData {
  timestamp: number;
  models: Array<{
    id: string;
    name: string;
    contextLength: number;
    hasVision: boolean;
    supportsJson: boolean;
    promptPricePerM: number;
    completionPricePerM: number;
  }>;
}

let memoryCache: CachedData | null = null;
const CACHE_TTL_MS = 24 * 60 * 60 * 1000; // 24 hours

const FALLBACK_MODELS = [
  {
    id: "anthropic/claude-sonnet-5.5",
    name: "Anthropic: Claude Sonnet 5.5",
    contextLength: 1000000,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 2.0,
    completionPricePerM: 10.0,
  },
  {
    id: "anthropic/claude-opus-5.5",
    name: "Anthropic: Claude Opus 5.5",
    contextLength: 1000000,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 4.0,
    completionPricePerM: 20.0,
  },
  {
    id: "anthropic/claude-haiku-5.5",
    name: "Anthropic: Claude Haiku 5.5",
    contextLength: 1000000,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 0.1,
    completionPricePerM: 0.5,
  },
  {
    id: "deepseek/deepseek-v4.1-flash-1017",
    name: "DeepSeek: DeepSeek V4.1 Flash",
    contextLength: 1048576,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 0.006,
    completionPricePerM: 0.5,
  },
  {
    id: "openai/gpt-6.1-sol",
    name: "OpenAI: GPT-6.1 Sol",
    contextLength: 1050000,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 2.0,
    completionPricePerM: 10.0,
  },
  {
    id: "openai/gpt-6-luna",
    name: "OpenAI: GPT-6 Luna",
    contextLength: 1050000,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 0.1,
    completionPricePerM: 0.5,
  },
  {
    id: "google/gemini-4-argon",
    name: "Google: Gemini 4 Argon",
    contextLength: 1048576,
    hasVision: true,
    supportsJson: true,
    promptPricePerM: 2.0,
    completionPricePerM: 10.0,
  },
];

async function getOrFetchModels(): Promise<CachedData["models"]> {
  const now = Date.now();
  if (memoryCache && now - memoryCache.timestamp < CACHE_TTL_MS) {
    return memoryCache.models;
  }

  try {
    const res = await fetch("https://openrouter.ai/api/v1/models", {
      headers: {
        "HTTP-Referer": "https://fluentescritor.app",
        "X-Title": "FluentEscritor",
      },
      next: { revalidate: 86400 }, // 24h Next.js cache hint
      signal: AbortSignal.timeout(15_000),
    });

    if (!res.ok) {
      if (memoryCache) return memoryCache.models;
      return FALLBACK_MODELS;
    }

    const data = (await res.json()) as { data?: OpenRouterModel[] };
    if (!Array.isArray(data.data)) {
      if (memoryCache) return memoryCache.models;
      return FALLBACK_MODELS;
    }

    const mapped = data.data
      .filter((m) => m && typeof m.id === "string" && !m.id.includes(":batch"))
      .map((m) => {
        const params = m.supported_parameters || [];
        const supportsJson =
          params.includes("response_format") || params.includes("structured_outputs");
        const modalities = m.architecture?.input_modalities || [];
        const hasVision = modalities.includes("image");
        const pPrompt = Number(m.pricing?.prompt || 0) * 1_000_000;
        const pComp = Number(m.pricing?.completion || 0) * 1_000_000;

        return {
          id: m.id,
          name: m.name || m.id,
          contextLength: m.context_length || 0,
          hasVision,
          supportsJson,
          promptPricePerM: Number(pPrompt.toFixed(4)),
          completionPricePerM: Number(pComp.toFixed(4)),
        };
      })
      .filter((m) => m.supportsJson);

    // Sort by name/id
    mapped.sort((a, b) => a.id.localeCompare(b.id));

    memoryCache = {
      timestamp: now,
      models: mapped.length > 0 ? mapped : FALLBACK_MODELS,
    };
    return memoryCache.models;
  } catch {
    if (memoryCache) return memoryCache.models;
    return FALLBACK_MODELS;
  }
}

export async function GET(request: Request) {
  try {
    const user = await getCurrentUser();
    if (!user) return unauthorized();
    const { searchParams } = new URL(request.url);
    const filterType = searchParams.get("type"); // "vision" | "all"

    const allModels = await getOrFetchModels();

    if (filterType === "vision") {
      const visionModels = allModels.filter((m) => m.hasVision);
      return NextResponse.json({ models: visionModels });
    }

    return NextResponse.json({ models: allModels });
  } catch (err) {
    return NextResponse.json(
      { error: err instanceof Error ? err.message : "Failed to fetch models" },
      { status: 500 }
    );
  }
}
