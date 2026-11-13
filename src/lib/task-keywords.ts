import type { TaskType } from "./types";

export interface KeywordGroup {
  label: string;
  keywords: string[];
}

const TASK1_KEYWORDS: Record<string, KeywordGroup[]> = {
  line: [
    {
      label: "Overview & Framing",
      keywords: ["overall", "broadly speaking", "trajectory", "over the period", "notably"],
    },
    {
      label: "Trends & Change",
      keywords: [
        "surge",
        "climb steadily",
        "peak at",
        "plummet",
        "dip",
        "level off",
        "fluctuate",
      ],
    },
    {
      label: "Degree & Speed",
      keywords: ["markedly", "gradually", "marginally", "sharp", "moderate"],
    },
    {
      label: "Comparison & Contrast",
      keywords: ["whereas", "conversely", "overtake", "outpace", "in contrast"],
    },
  ],
  bar: [
    {
      label: "Overview & Framing",
      keywords: ["overall", "across all categories", "most pronounced", "disparity", "notably"],
    },
    {
      label: "Comparisons & Ranking",
      keywords: ["outnumber", "exceed", "lag behind", "roughly comparable", "respectively"],
    },
    {
      label: "Proportions & Figures",
      keywords: ["account for", "represent", "just under", "approximately", "marginal"],
    },
    {
      label: "Contrast & Linking",
      keywords: ["whereas", "in contrast", "by comparison", "conversely"],
    },
  ],
  pie: [
    {
      label: "Overview & Proportions",
      keywords: [
        "overall",
        "comprise",
        "account for",
        "the vast majority",
        "a negligible share",
      ],
    },
    {
      label: "Shifts & Comparisons",
      keywords: ["double", "halve", "remain static", "surpass", "respectively"],
    },
    {
      label: "Approximation",
      keywords: ["roughly", "just over", "nearly", "one-third", "proportion"],
    },
    {
      label: "Contrast & Linking",
      keywords: ["meanwhile", "whereas", "in contrast", "conversely"],
    },
  ],
  table: [
    {
      label: "Overview & Synthesis",
      keywords: ["overall", "stand out", "across the board", "disparity", "notably"],
    },
    {
      label: "Comparisons & Ranking",
      keywords: ["exceed", "trail behind", "comparable", "respectively", "rank highest"],
    },
    {
      label: "Figures & Degree",
      keywords: ["substantially", "marginally", "approximately", "account for", "steadily"],
    },
    {
      label: "Contrast & Linking",
      keywords: ["whereas", "by contrast", "conversely", "simultaneously"],
    },
  ],
  mixed: [
    {
      label: "Overview & Dual Framing",
      keywords: ["overall", "correlation", "in tandem", "notably", "despite"],
    },
    {
      label: "Trends & Figures",
      keywords: ["climb", "decline", "peak at", "account for", "fluctuate"],
    },
    {
      label: "Comparisons",
      keywords: ["exceed", "outpace", "proportionally", "respectively", "comparable"],
    },
    {
      label: "Contrast & Linking",
      keywords: ["whereas", "conversely", "meanwhile", "in contrast"],
    },
  ],
  process: [
    {
      label: "Overview & Framing",
      keywords: ["overall", "cyclical", "linear", "consist of", "culminate in"],
    },
    {
      label: "Sequencing Stages",
      keywords: ["initially", "subsequently", "thereafter", "simultaneously", "eventually"],
    },
    {
      label: "Action & Transformation",
      keywords: ["undergo", "convert into", "extract", "filter", "transport"],
    },
    {
      label: "Purpose & Result",
      keywords: ["in order to", "thereby", "ensuring that", "once completed"],
    },
  ],
  map: [
    {
      label: "Overview & Transformation",
      keywords: ["overall", "undergo", "modernization", "expand", "replace"],
    },
    {
      label: "Spatial & Location",
      keywords: ["adjacent to", "to the north of", "in the vicinity of", "alongside", "central"],
    },
    {
      label: "Changes & Additions",
      keywords: ["construct", "demolish", "redevelop", "convert into", "remain intact"],
    },
    {
      label: "Contrast & Linking",
      keywords: ["whereas", "meanwhile", "in place of", "previously"],
    },
  ],
};

const TASK2_KEYWORDS: Record<string, KeywordGroup[]> = {
  opinion: [
    {
      label: "Thesis & Stance",
      keywords: ["convinced that", "predominantly", "to a large extent", "untenable", "compelling"],
    },
    {
      label: "Supporting Arguments",
      keywords: ["foster", "undermine", "indispensable", "catalyst", "long-term"],
    },
    {
      label: "Concession & Contrast",
      keywords: ["admittedly", "nevertheless", "conversely", "albeit", "notwithstanding"],
    },
    {
      label: "Hedging & Conclusion",
      keywords: ["tend to", "arguably", "in conclusion", "ultimately", "paramount"],
    },
  ],
  discussion: [
    {
      label: "Framing Both Views",
      keywords: [
        "proponents argue",
        "critics contend",
        "from another perspective",
        "merit",
        "drawback",
      ],
    },
    {
      label: "Weighing & Stance",
      keywords: ["on balance", "convincing", "outweigh", "warranted", "nuanced"],
    },
    {
      label: "Contrast & Concession",
      keywords: ["whereas", "conversely", "admittedly", "nevertheless", "however"],
    },
    {
      label: "Hedging & Conclusion",
      keywords: ["arguably", "tend to", "ultimately", "in summary"],
    },
  ],
  advantages: [
    {
      label: "Weighing Trade-Offs",
      keywords: ["outweigh", "merit", "drawback", "trade-off", "on balance"],
    },
    {
      label: "Positive Impact",
      keywords: ["foster", "enhance", "facilitate", "viable", "long-term"],
    },
    {
      label: "Negative Impact",
      keywords: ["exacerbate", "undermine", "detrimental", "inadvertently", "short-sighted"],
    },
    {
      label: "Contrast & Hedging",
      keywords: ["conversely", "nevertheless", "albeit", "arguably", "tend to"],
    },
  ],
  problem_solution: [
    {
      label: "Root Causes",
      keywords: ["stem from", "driven by", "exacerbate", "underlying", "compounded by"],
    },
    {
      label: "Consequences",
      keywords: ["detrimental", "inevitably", "give rise to", "far-reaching", "pressing"],
    },
    {
      label: "Solutions & Policy",
      keywords: ["mitigate", "alleviate", "curb", "incentivize", "implement"],
    },
    {
      label: "Feasibility & Conclusion",
      keywords: ["viable", "concerted effort", "sustainable", "ultimately"],
    },
  ],
  two_part: [
    {
      label: "Analysis & Causes",
      keywords: ["stem from", "attribute to", "primarily", "underlying", "multifaceted"],
    },
    {
      label: "Evaluation & Impact",
      keywords: ["beneficial", "detrimental", "profound", "widespread", "inevitably"],
    },
    {
      label: "Contrast & Nuance",
      keywords: ["whereas", "admittedly", "nevertheless", "to some extent", "conversely"],
    },
    {
      label: "Hedging & Conclusion",
      keywords: ["arguably", "tend to", "in conclusion", "ultimately"],
    },
  ],
};

export function getTaskKeywords(taskType: TaskType, category: string): KeywordGroup[] {
  const bank = taskType === "task1" ? TASK1_KEYWORDS : TASK2_KEYWORDS;
  if (bank[category]) {
    return bank[category];
  }
  return taskType === "task1" ? TASK1_KEYWORDS.line : TASK2_KEYWORDS.opinion;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function isKeywordUsedInText(text: string, keyword: string): boolean {
  if (!text || !keyword) return false;
  const pattern = escapeRegExp(keyword.trim()).replace(/\s+/g, "\\s+");
  const regex = new RegExp(`\\b${pattern}\\b`, "i");
  return regex.test(text);
}
