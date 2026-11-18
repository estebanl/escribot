import { useState } from "react";
import {
  ResponsiveContainer,
  LineChart,
  Line,
  BarChart,
  Bar,
  PieChart,
  Pie,
  Cell,
  ComposedChart,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
} from "recharts";
import { ArrowRight, RotateCw, ZoomIn, X, Compass, BarChart2, FileText } from "lucide-react";
import { normalizeChartSpec } from "@/lib/chart-utils";
import type {
  ChartSpec,
  ComparisonMapSpec,
  MapFeatureCategory,
  MapZoneId,
} from "@/lib/types";

const CHART_PALETTE = [
  "#16656e", // Oxford Teal
  "#c55e1b", // Terracotta Amber
  "#2b7a52", // Forest Emerald
  "#475569", // Slate Ink
  "#9a6b25", // Ochre Gold
  "#9e3a2b", // Brick Crimson
];

const MAP_CATEGORY_STYLES: Record<
  MapFeatureCategory,
  { bg: string; border: string; text: string; tag: string }
> = {
  nature: {
    bg: "#e6f4ea",
    border: "#68a87b",
    text: "#1b4d2e",
    tag: "Green / Rural",
  },
  residential: {
    bg: "#fef3e2",
    border: "#d99b4e",
    text: "#5c390c",
    tag: "Housing",
  },
  commercial: {
    bg: "#e3f2f6",
    border: "#3b8ea5",
    text: "#134554",
    tag: "Commercial",
  },
  industrial: {
    bg: "#eceff2",
    border: "#7c8998",
    text: "#2c3642",
    tag: "Industrial",
  },
  transport: {
    bg: "#f5ebe6",
    border: "#b56a4c",
    text: "#522514",
    tag: "Transit",
  },
  leisure: {
    bg: "#e2f5f0",
    border: "#2e9c84",
    text: "#104a3d",
    tag: "Leisure",
  },
  civic: {
    bg: "#eef2f8",
    border: "#5a769e",
    text: "#1f3452",
    tag: "Civic / Edu",
  },
};

const ORDERED_ZONES: MapZoneId[] = [
  "NW",
  "N",
  "NE",
  "W",
  "C",
  "E",
  "SW",
  "S",
  "SE",
];

function MapGridCard({
  periodLabel,
  spec,
  features,
}: {
  periodLabel: string;
  spec: ComparisonMapSpec;
  features: ComparisonMapSpec["featuresB"];
}) {
  const byZone = new Map(features.map((f) => [f.zone, f]));

  return (
    <div className="rounded-xl border border-slate-300 bg-white text-slate-900 p-3.5 shadow-xs">
      <div className="flex items-center justify-between mb-2.5 pb-1.5 border-b border-slate-200">
        <span className="font-mono text-xs font-bold uppercase tracking-wider px-2 py-0.5 rounded bg-slate-800 text-white">
          {periodLabel}
        </span>
        <span className="flex items-center gap-1 text-[11px] font-mono text-slate-500">
          <Compass className="w-3.5 h-3.5" /> N ↑
        </span>
      </div>

      <div className="relative grid grid-cols-3 gap-2">
        {ORDERED_ZONES.map((zone) => {
          const item = byZone.get(zone);
          const style =
            MAP_CATEGORY_STYLES[item?.category || "civic"] ||
            MAP_CATEGORY_STYLES.civic;
          return (
            <div
              key={zone}
              style={{
                backgroundColor: style.bg,
                borderColor: style.border,
                color: style.text,
              }}
              className="relative rounded-lg border p-2.5 min-h-[78px] flex flex-col justify-between"
            >
              <div className="flex items-center justify-between gap-1">
                <span className="text-[10px] font-mono font-semibold opacity-75">
                  {zone}
                </span>
                {item?.changed && (
                  <span className="text-[9px] font-mono uppercase px-1.5 py-0.2 rounded bg-amber-600 text-white font-semibold">
                    Changed
                  </span>
                )}
              </div>
              <p className="text-xs font-semibold leading-snug mt-1">
                {item?.label || "Open Land"}
              </p>
              <span className="text-[10px] opacity-75 mt-1">{style.tag}</span>
            </div>
          );
        })}
      </div>

      {spec.mainRoad === "horizontal" && (
        <div className="mt-2 py-1 px-2.5 rounded bg-slate-200 text-slate-700 text-[11px] font-mono text-center">
          ═══ Main East–West Arterial Road (bisects Central Corridor) ═══
        </div>
      )}

      {spec.waterway === "coastal_south" && (
        <div className="mt-2 py-1.5 px-3 rounded-lg bg-sky-100 border border-sky-300 text-sky-900 text-xs font-mono text-center">
          ≈≈≈ Southern Harbour &amp; Seafront Channel ≈≈≈
        </div>
      )}
    </div>
  );
}

function SpecContent({
  spec,
  compact = false,
  isNested = false,
}: {
  spec: ChartSpec;
  compact?: boolean;
  isNested?: boolean;
}) {
  const height = compact ? 235 : 305;
  const marginClass = isNested ? "" : "mt-4";

  /* ---------- 1. LINE / BAR / MIXED CHARTS ---------- */
  if (spec.kind === "line" || spec.kind === "bar" || spec.kind === "mixed") {
    if (!Array.isArray(spec.data) || spec.data.length === 0 || !Array.isArray(spec.series) || spec.series.length === 0) {
      return null;
    }
    const yUnit = spec.yAxisUnit || undefined;
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-white text-slate-900 p-4 shadow-xs`}>
        <div className="mb-3">
          <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
          {spec.subtitle && (
            <p className="text-xs text-slate-600 mt-0.5">{spec.subtitle}</p>
          )}
        </div>

        <div style={{ width: "100%", height }}>
          <ResponsiveContainer width="100%" height="100%">
            {spec.kind === "line" ? (
              <LineChart data={spec.data} margin={{ top: 8, right: 16, left: 4, bottom: 4 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                <XAxis dataKey={spec.xAxisKey} tick={{ fontSize: 12, fill: "#334155" }} />
                <YAxis
                  unit={yUnit}
                  tick={{ fontSize: 12, fill: "#334155" }}
                  width="auto"
                />
                <Tooltip
                  contentStyle={{
                    backgroundColor: "#ffffff",
                    borderColor: "#cbd5e1",
                    fontSize: "12px",
                    borderRadius: "8px",
                  }}
                />
                <Legend wrapperStyle={{ fontSize: "12px" }} />
                {spec.series.map((s, i) => (
                  <Line
                    key={s.key}
                    type="monotone"
                    dataKey={s.key}
                    name={s.label}
                    stroke={s.color || CHART_PALETTE[i % CHART_PALETTE.length]}
                    strokeWidth={2.5}
                    dot={{ r: 3.5 }}
                    activeDot={{ r: 5 }}
                  />
                ))}
              </LineChart>
            ) : spec.kind === "bar" ? (
              <BarChart data={spec.data} margin={{ top: 8, right: 16, left: 4, bottom: 4 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                <XAxis dataKey={spec.xAxisKey} tick={{ fontSize: 11, fill: "#334155" }} />
                <YAxis
                  unit={yUnit}
                  tick={{ fontSize: 12, fill: "#334155" }}
                  width="auto"
                />
                <Tooltip
                  contentStyle={{
                    backgroundColor: "#ffffff",
                    borderColor: "#cbd5e1",
                    fontSize: "12px",
                    borderRadius: "8px",
                  }}
                />
                <Legend wrapperStyle={{ fontSize: "12px" }} />
                {spec.series.map((s, i) => (
                  <Bar
                    key={s.key}
                    dataKey={s.key}
                    name={s.label}
                    fill={s.color || CHART_PALETTE[i % CHART_PALETTE.length]}
                    radius={[4, 4, 0, 0]}
                  />
                ))}
              </BarChart>
            ) : (
              <ComposedChart data={spec.data} margin={{ top: 8, right: 16, left: 4, bottom: 4 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
                <XAxis dataKey={spec.xAxisKey} tick={{ fontSize: 12, fill: "#334155" }} />
                <YAxis
                  yAxisId="left"
                  unit={yUnit ? ` ${yUnit}` : undefined}
                  tick={{ fontSize: 11, fill: "#334155" }}
                  width="auto"
                />
                <YAxis
                  yAxisId="right"
                  orientation="right"
                  unit={spec.yAxisRightUnit || "%"}
                  tick={{ fontSize: 11, fill: "#334155" }}
                  width="auto"
                />
                <Tooltip
                  contentStyle={{
                    backgroundColor: "#ffffff",
                    borderColor: "#cbd5e1",
                    fontSize: "12px",
                    borderRadius: "8px",
                  }}
                />
                <Legend wrapperStyle={{ fontSize: "12px" }} />
                {spec.series.map((s, i) =>
                  s.type === "bar" ? (
                    <Bar
                      key={s.key}
                      yAxisId={s.yAxisId || "left"}
                      dataKey={s.key}
                      name={s.label}
                      fill={s.color || CHART_PALETTE[i % CHART_PALETTE.length]}
                      radius={[4, 4, 0, 0]}
                      barSize={28}
                    />
                  ) : (
                    <Line
                      key={s.key}
                      yAxisId={s.yAxisId || "right"}
                      type="monotone"
                      dataKey={s.key}
                      name={s.label}
                      stroke={s.color || CHART_PALETTE[i % CHART_PALETTE.length]}
                      strokeWidth={2.5}
                      dot={{ r: 4 }}
                    />
                  )
                )}
              </ComposedChart>
            )}
          </ResponsiveContainer>
        </div>

        {spec.sourceNote && (
          <p className="text-[11px] text-slate-500 mt-2 font-mono">{spec.sourceNote}</p>
        )}
      </div>
    );
  }

  /* ---------- 2. DUAL / COMPARATIVE PIE CHARTS ---------- */
  if (spec.kind === "pie") {
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-white text-slate-900 p-4 shadow-xs`}>
        <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
        {spec.subtitle && (
          <p className="text-xs text-slate-600 mt-0.5 mb-3">{spec.subtitle}</p>
        )}

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          {spec.charts.map((chart) => (
            <div
              key={chart.label}
              className="rounded-lg border border-slate-200 p-3 bg-slate-50/60"
            >
              <p className="text-xs font-mono font-semibold text-center text-slate-800 mb-1">
                {chart.label}
              </p>
              <div style={{ width: "100%", height: 190 }}>
                <ResponsiveContainer width="100%" height="100%">
                  <PieChart>
                    <Pie
                      data={chart.slices}
                      dataKey="value"
                      nameKey="name"
                      cx="50%"
                      cy="50%"
                      outerRadius={66}
                      label={({ value }) => `${value}${spec.unit}`}
                      labelLine={false}
                    >
                      {chart.slices.map((_, idx) => (
                        <Cell
                          key={idx}
                          fill={CHART_PALETTE[idx % CHART_PALETTE.length]}
                        />
                      ))}
                    </Pie>
                    <Tooltip
                      formatter={(val) => `${val}${spec.unit}`}
                      contentStyle={{ fontSize: "12px", borderRadius: "8px" }}
                    />
                  </PieChart>
                </ResponsiveContainer>
              </div>
              <div className="space-y-1 mt-1">
                {chart.slices.map((s, idx) => (
                  <div
                    key={s.name}
                    className="flex items-center justify-between text-xs text-slate-700"
                  >
                    <span className="flex items-center gap-1.5 truncate">
                      <span
                        className="w-2.5 h-2.5 rounded-xs shrink-0"
                        style={{
                          backgroundColor: CHART_PALETTE[idx % CHART_PALETTE.length],
                        }}
                      />
                      <span className="truncate">{s.name}</span>
                    </span>
                    <span className="font-mono font-semibold">
                      {s.value}
                      {spec.unit}
                    </span>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>

        {spec.sourceNote && (
          <p className="text-[11px] text-slate-500 mt-3 font-mono">{spec.sourceNote}</p>
        )}
      </div>
    );
  }

  /* ---------- 3. DATA TABLE ---------- */
  if (spec.kind === "table") {
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-white text-slate-900 p-4 shadow-xs overflow-x-auto`}>
        <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
        {spec.unitNote && (
          <p className="text-xs text-slate-600 mt-0.5 mb-3">{spec.unitNote}</p>
        )}

        <table className="w-full text-left border-collapse text-xs sm:text-sm tabular-nums">
          <thead>
            <tr className="border-b-2 border-slate-300 bg-slate-100">
              {spec.columns.map((col) => (
                <th
                  key={col}
                  className="py-2.5 px-3 font-semibold text-slate-800 whitespace-nowrap"
                >
                  {col}
                </th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-200">
            {spec.rows.map((row, rIdx) => (
              <tr key={rIdx} className="hover:bg-slate-50">
                {row.map((cell, cIdx) => (
                  <td
                    key={cIdx}
                    className={`py-2.5 px-3 ${
                      cIdx === 0
                        ? "font-semibold text-slate-900"
                        : "font-mono text-slate-700"
                    }`}
                  >
                    {cell}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>

        {spec.sourceNote && (
          <p className="text-[11px] text-slate-500 mt-3 font-mono">{spec.sourceNote}</p>
        )}
      </div>
    );
  }

  /* ---------- 4. PROCESS / LIFECYCLE DIAGRAM ---------- */
  if (spec.kind === "process") {
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-white text-slate-900 p-4 shadow-xs`}>
        <div className="flex items-start justify-between gap-2 mb-3">
          <div>
            <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
            {spec.subtitle && (
              <p className="text-xs text-slate-600 mt-0.5">{spec.subtitle}</p>
            )}
          </div>
          {spec.isCyclic && (
            <span className="inline-flex items-center gap-1 px-2 py-1 rounded-md bg-teal-50 border border-teal-300 text-teal-900 text-[11px] font-mono font-semibold shrink-0">
              <RotateCw className="w-3 h-3" /> Closed-Loop Cycle
            </span>
          )}
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          {spec.steps.map((step, idx) => (
            <div
              key={step.stepNumber}
              className="relative rounded-lg border border-slate-200 bg-slate-50 p-3 flex flex-col justify-between"
            >
              <div>
                <div className="flex items-center justify-between gap-2 mb-1.5">
                  <span className="inline-flex items-center justify-center px-2 py-0.5 rounded bg-slate-800 text-white font-mono text-[11px] font-bold">
                    Stage {step.stepNumber}
                  </span>
                  {step.badge && (
                    <span className="text-[10px] font-mono px-1.5 py-0.5 rounded bg-amber-100 text-amber-900 font-medium">
                      {step.badge}
                    </span>
                  )}
                </div>
                <p className="text-xs font-bold text-slate-900">{step.stageName}</p>
                <p className="text-xs text-slate-600 leading-relaxed mt-1">
                  {step.detail}
                </p>
              </div>
              {idx < spec.steps.length - 1 && (
                <div className="mt-2 pt-1.5 border-t border-slate-200/80 flex items-center justify-end text-[10px] font-mono text-slate-500">
                  <span>Proceeds to Stage {step.stepNumber + 1}</span>
                  <ArrowRight className="w-3 h-3 ml-1" />
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    );
  }

  /* ---------- 5. TWO-PERIOD COMPARISON MAP ---------- */
  if (spec.kind === "map") {
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-slate-50 text-slate-900 p-4 shadow-xs space-y-3`}>
        <div>
          <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
          {spec.subtitle && (
            <p className="text-xs text-slate-600 mt-0.5">{spec.subtitle}</p>
          )}
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-2 gap-3.5">
          <MapGridCard
            periodLabel={spec.periodA}
            spec={spec}
            features={spec.featuresA}
          />
          <MapGridCard
            periodLabel={spec.periodB}
            spec={spec}
            features={spec.featuresB}
          />
        </div>
      </div>
    );
  }

  /* ---------- 6. COMPOSITE VISUAL (COMBINED CHARTS & TABLES) ---------- */
  if (spec.kind === "composite") {
    return (
      <div className={`${marginClass} rounded-xl border border-slate-300 bg-white text-slate-900 p-4 shadow-xs space-y-4`}>
        <div>
          <h3 className="font-sans text-sm font-bold text-slate-900">{spec.title}</h3>
          {spec.subtitle && (
            <p className="text-xs text-slate-600 mt-0.5">{spec.subtitle}</p>
          )}
        </div>

        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">
          {spec.visuals.map((subVisual, idx) => (
            <div key={idx} className="w-full">
              <SpecContent spec={subVisual} compact={compact} isNested />
            </div>
          ))}
        </div>

        {spec.sourceNote && (
          <p className="text-[11px] text-slate-500 mt-2 font-mono">{spec.sourceNote}</p>
        )}
      </div>
    );
  }

  return null;
}

export function TaskVisualRenderer({
  chartSpecJson,
  chartSpecDirect,
  imageDataUrl,
  compact = false,
  isNested = false,
}: {
  chartSpecJson?: string | null;
  chartSpecDirect?: ChartSpec | null;
  imageDataUrl?: string | null;
  compact?: boolean;
  isNested?: boolean;
}) {
  const [zoomed, setZoomed] = useState(false);
  const [activeTab, setActiveTab] = useState<"spec" | "image">("spec");

  let rawSpec: unknown = chartSpecDirect || null;
  if (!rawSpec && chartSpecJson) {
    try {
      rawSpec = JSON.parse(chartSpecJson);
    } catch {
      rawSpec = null;
    }
  }
  const spec: ChartSpec | null = normalizeChartSpec(rawSpec) ?? null;

  if (!imageDataUrl && !spec) return null;

  const renderImage = () => (
    <div className={isNested ? "" : "mt-4"}>
      <div className="relative rounded-xl border border-line bg-white p-3 overflow-hidden group">
        <img
          src={imageDataUrl!}
          alt="Academic IELTS Task 1 official prompt visual or diagram"
          className="w-full max-h-[420px] object-contain mx-auto rounded"
        />
        <button
          type="button"
          onClick={() => setZoomed(true)}
          className="absolute top-4 right-4 flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg bg-slate-900/80 text-white text-xs font-medium hover:bg-slate-900 transition cursor-pointer"
        >
          <ZoomIn className="w-3.5 h-3.5" />
          <span>Inspect Full Size</span>
        </button>
      </div>

      {zoomed && (
        <div
          onClick={() => setZoomed(false)}
          className="fixed inset-0 z-50 bg-black/80 backdrop-blur-xs flex items-center justify-center p-4"
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="relative max-w-5xl w-full bg-white rounded-2xl p-4 shadow-2xl"
          >
            <button
              type="button"
              onClick={() => setZoomed(false)}
              className="absolute top-3 right-3 p-2 rounded-full bg-slate-900 text-white hover:bg-slate-700 cursor-pointer"
            >
              <X className="w-4 h-4" />
            </button>
            <img
              src={imageDataUrl!}
              alt="Zoomed Task 1 visual"
              className="w-full max-h-[84vh] object-contain mx-auto"
            />
          </div>
        </div>
      )}
    </div>
  );

  // If only image is available
  if (imageDataUrl && !spec) {
    return renderImage();
  }

  // If only spec is available
  if (!imageDataUrl && spec) {
    return <SpecContent spec={spec} compact={compact} isNested={isNested} />;
  }

  // If BOTH are available (an uploaded image plus an extracted spec)
  return (
    <div className={isNested ? "" : "mt-4"}>
      <div className="flex items-center justify-between gap-2 mb-2 px-1">
        <span className="text-[11px] font-mono uppercase tracking-wider text-slate-500 font-semibold">
          Task 1 Visual Material
        </span>
        <div className="inline-flex items-center gap-1 p-0.5 rounded-lg bg-slate-100 border border-slate-200">
          <button
            type="button"
            onClick={() => setActiveTab("spec")}
            className={`flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs transition cursor-pointer ${
              activeTab === "spec"
                ? "bg-white text-slate-900 shadow-xs font-semibold"
                : "text-slate-600 hover:text-slate-900"
            }`}
          >
            <BarChart2 className="w-3.5 h-3.5 text-teal-700" />
            <span>Interactive Visual</span>
          </button>
          <button
            type="button"
            onClick={() => setActiveTab("image")}
            className={`flex items-center gap-1.5 px-2.5 py-1 rounded-md text-xs transition cursor-pointer ${
              activeTab === "image"
                ? "bg-white text-slate-900 shadow-xs font-semibold"
                : "text-slate-600 hover:text-slate-900"
            }`}
          >
            <FileText className="w-3.5 h-3.5 text-slate-600" />
            <span>Original Exam Paper</span>
          </button>
        </div>
      </div>

      {activeTab === "image" ? (
        renderImage()
      ) : (
        <SpecContent spec={spec!} compact={compact} isNested={true} />
      )}
    </div>
  );
}
