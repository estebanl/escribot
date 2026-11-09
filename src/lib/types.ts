export type TaskType = "task1" | "task2";

export type Task1Category =
  | "line"
  | "bar"
  | "pie"
  | "table"
  | "mixed"
  | "process"
  | "map";

export type Task2Category =
  | "opinion"
  | "discussion"
  | "advantages"
  | "problem_solution"
  | "two_part";

export type PromptCategory = Task1Category | Task2Category;

export type AIProviderId =
  | "openrouter"
  | "deepseek"
  | "gemini"
  | "openai"
  | "anthropic";

export type NativeLanguageCode =
  | "es"
  | "pt"
  | "fr"
  | "zh"
  | "ar"
  | "hi"
  | "neutral";

export type SessionMode =
  | "coach"
  | "simulator_t1"
  | "simulator_t2"
  | "simulator_full_60m";

export type CriterionKey = "TA_TR" | "CC" | "LR" | "GRA";
export type AnnotationCategory = CriterionKey | "L1";

/* ---------- Structured Task 1 Visual Specifications ---------- */

export interface CartesianChartSpec {
  kind: "line" | "bar" | "mixed";
  title: string;
  subtitle?: string;
  xAxisKey: string;
  yAxisUnit: string;
  yAxisRightUnit?: string;
  series: Array<{
    key: string;
    label: string;
    type?: "line" | "bar";
    yAxisId?: "left" | "right";
    color?: string;
  }>;
  data: Array<Record<string, string | number>>;
  sourceNote?: string;
}

export interface PieChartSpec {
  kind: "pie";
  title: string;
  subtitle?: string;
  unit: string;
  charts: Array<{
    label: string;
    slices: Array<{ name: string; value: number }>;
  }>;
  sourceNote?: string;
}

export interface TableChartSpec {
  kind: "table";
  title: string;
  subtitle?: string;
  unitNote?: string;
  columns: string[];
  rows: Array<Array<string | number>>;
  sourceNote?: string;
}

export interface ProcessDiagramSpec {
  kind: "process";
  title: string;
  subtitle?: string;
  isCyclic?: boolean;
  steps: Array<{
    stepNumber: number;
    stageName: string;
    detail: string;
    badge?: string;
  }>;
}

export type MapZoneId =
  | "NW"
  | "N"
  | "NE"
  | "W"
  | "C"
  | "E"
  | "SW"
  | "S"
  | "SE";

export type MapFeatureCategory =
  | "residential"
  | "nature"
  | "commercial"
  | "industrial"
  | "transport"
  | "leisure"
  | "civic";

export interface MapZoneFeature {
  zone: MapZoneId;
  label: string;
  category: MapFeatureCategory;
  changed?: boolean;
}

export interface ComparisonMapSpec {
  kind: "map";
  title: string;
  subtitle?: string;
  periodA: string;
  periodB: string;
  waterway?: "coastal_south" | "river_vertical" | "none";
  mainRoad?: "horizontal" | "vertical" | "ring" | "none";
  featuresA: MapZoneFeature[];
  featuresB: MapZoneFeature[];
}

export interface CompositeChartSpec {
  kind: "composite";
  title: string;
  subtitle?: string;
  visuals: Array<PieChartSpec | TableChartSpec | CartesianChartSpec>;
  sourceNote?: string;
}

export type ChartSpec =
  | CartesianChartSpec
  | PieChartSpec
  | TableChartSpec
  | ProcessDiagramSpec
  | ComparisonMapSpec
  | CompositeChartSpec;

/* ---------- Coaching, Outline & Rubric Evaluation Types ---------- */

export interface OutlineInput {
  thesisOrOverview: string;
  body1Plan: string;
  body2Plan: string;
  targetKeyTerms?: string;
}

export interface OutlineFeedback {
  readiness: "strong" | "needs_refinement" | "off_track";
  estimatedTaskBandCeiling: number;
  verdictSummary: string;
  overviewOrThesisCheck: string;
  bodyGroupingCheck: string;
  actionableTweaks: string[];
  suggestedAcademicCollocations: Array<{
    phrase: string;
    usageNote: string;
  }>;
}

export interface CriterionAssessment {
  band: number;
  descriptorSummary: string;
  strengths: string[];
  limitingFactors: string[];
  howToGainHalfBand: string;
}

export interface InlineAnnotation {
  id: string;
  category: AnnotationCategory;
  severity: "error" | "upgrade";
  originalSpan: string;
  replacement: string;
  explanation: string;
}

export interface ExtractedVaultCandidate {
  itemType: "error" | "collocation";
  criterion: AnnotationCategory;
  originalText: string;
  upgradedText: string;
  explanation: string;
  exampleSentence: string;
}

export interface EvaluationPayload {
  overallBand: number;
  taTrBand: number;
  ccBand: number;
  lrBand: number;
  graBand: number;
  examinerSummary: string;
  criteria: {
    TA_TR: CriterionAssessment;
    CC: CriterionAssessment;
    LR: CriterionAssessment;
    GRA: CriterionAssessment;
  };
  inlineAnnotations: InlineAnnotation[];
  targetBandRewrite: string;
  rewriteKeyChanges: string[];
  topPriorities: string[];
  vaultItems: ExtractedVaultCandidate[];
}

export interface ToolkitRequest {
  tool: "collocations" | "l1_check" | "paragraph_check";
  taskType: TaskType;
  promptText: string;
  selectedText: string;
  fullEssayContext?: string;
}

export interface ToolkitResponse {
  tool: "collocations" | "l1_check" | "paragraph_check";
  headline: string;
  analysis: string;
  suggestions: Array<{
    label: string;
    rewrittenOrPhrase: string;
    rationale: string;
  }>;
}

export type DrillType =
  | "overview_sprint"
  | "cohesion"
  | "l1_surgeon"
  | "lexical_hedging";

export interface MicroDrillChallenge {
  drillType: DrillType;
  targetCriterion: AnnotationCategory;
  title: string;
  instructions: string;
  timeLimitSeconds: number;
  sourceContext: string;
  chartSpec?: ChartSpec;
  flawedText?: string;
  hints: string[];
  referenceModelAnswer: string;
}

export interface MicroDrillScore {
  scoreOutOf10: number;
  estimatedBandImpact: string;
  feedback: string;
  improvedVersion: string;
  keyTakeaway: string;
}
