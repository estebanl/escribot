import type { CartesianChartSpec, ChartSpec } from "./types";

const DEFAULT_SERIES_COLORS = [
  "#16656e",
  "#c55e1b",
  "#2b7a52",
  "#475569",
  "#9a6b25",
  "#9e3a2b",
];

function toFiniteNumber(val: unknown): number | null {
  if (typeof val === "number") {
    return Number.isFinite(val) ? val : null;
  }
  if (typeof val === "string") {
    const cleaned = val.trim().replace(/,/g, "").replace(/%$/, "");
    if (!cleaned) return null;
    const num = Number(cleaned);
    return Number.isFinite(num) ? num : null;
  }
  return null;
}

function normalizeCartesianKind(rawKind: unknown): "line" | "bar" | "mixed" | null {
  if (typeof rawKind !== "string") return null;
  const k = rawKind.trim().toLowerCase();
  if (k === "line" || k === "linechart" || k === "line_chart") return "line";
  if (k === "bar" || k === "barchart" || k === "bar_chart" || k === "column") return "bar";
  if (k === "mixed" || k === "composed") return "mixed";
  return null;
}

/**
 * Normalizes and validates an AI-generated (or stored) CartesianChartSpec
 * ("line" | "bar" | "mixed") into the exact shape expected by TaskVisualRenderer.
 * Returns undefined if the input cannot be salvaged into a valid chart.
 */
export function normalizeCartesianChartSpec(raw: unknown): CartesianChartSpec | undefined {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return undefined;
  const obj = raw as Record<string, unknown>;

  const kind = normalizeCartesianKind(obj.kind ?? obj.type ?? obj.chartType);
  if (!kind) return undefined;

  const title =
    typeof obj.title === "string" && obj.title.trim() ? obj.title.trim() : "Data Chart";
  const subtitle =
    typeof obj.subtitle === "string" && obj.subtitle.trim()
      ? obj.subtitle.trim()
      : undefined;
  const sourceNote =
    typeof obj.sourceNote === "string" && obj.sourceNote.trim()
      ? obj.sourceNote.trim()
      : undefined;

  const rawUnit = obj.yAxisUnit ?? obj.unit ?? obj.yUnit ?? "";
  const yAxisUnit = typeof rawUnit === "string" ? rawUnit.trim() : "";
  const yAxisRightUnit =
    typeof obj.yAxisRightUnit === "string" && obj.yAxisRightUnit.trim()
      ? obj.yAxisRightUnit.trim()
      : undefined;

  // Handle Chart.js / Highcharts-style transposed data:
  // { labels/categories: [...], datasets/series: [{ label/name, data: [...] }] }
  const rawSeriesList = Array.isArray(obj.series)
    ? obj.series
    : Array.isArray(obj.datasets)
    ? obj.datasets
    : null;

  const rawLabels = Array.isArray(obj.labels)
    ? obj.labels
    : Array.isArray(obj.categories)
    ? obj.categories
    : Array.isArray(obj.xLabels)
    ? obj.xLabels
    : null;

  let workingData: Array<Record<string, unknown>> | null = null;
  let workingXKey: string | null = null;

  if (
    (!Array.isArray(obj.data) || obj.data.length === 0) &&
    rawSeriesList &&
    rawSeriesList.length > 0 &&
    typeof rawSeriesList[0] === "object" &&
    rawSeriesList[0] !== null &&
    Array.isArray((rawSeriesList[0] as Record<string, unknown>).data)
  ) {
    const firstSeriesData = (rawSeriesList[0] as Record<string, unknown>).data as unknown[];
    const pointCount = firstSeriesData.length;
    if (pointCount >= 2) {
      workingXKey = "label";
      const rows: Array<Record<string, unknown>> = [];
      for (let i = 0; i < pointCount; i++) {
        const row: Record<string, unknown> = {
          label: rawLabels && rawLabels[i] != null ? String(rawLabels[i]) : String(i + 1),
        };
        rawSeriesList.forEach((s, sIdx) => {
          if (s && typeof s === "object") {
            const sr = s as Record<string, unknown>;
            const sKey =
              typeof sr.key === "string" && sr.key.trim()
                ? sr.key.trim()
                : `s${sIdx + 1}`;
            const sData = Array.isArray(sr.data) ? sr.data : [];
            row[sKey] = sData[i];
          }
        });
        rows.push(row);
      }
      workingData = rows;
    }
  } else if (Array.isArray(obj.data) && obj.data.length > 0) {
    workingData = obj.data.filter(
      (d): d is Record<string, unknown> => Boolean(d && typeof d === "object" && !Array.isArray(d))
    );
  }

  if (!workingData || workingData.length < 2) return undefined;

  const sampleKeys = Object.keys(workingData[0]);
  if (sampleKeys.length < 2) return undefined;

  // Determine xAxisKey
  const candidateXKey = obj.xAxisKey ?? obj.xKey ?? obj.xAxis ?? workingXKey;
  let xAxisKey: string | undefined =
    typeof candidateXKey === "string" && sampleKeys.includes(candidateXKey)
      ? candidateXKey
      : undefined;

  if (!xAxisKey) {
    const commonXKeys = ["year", "xLabel", "label", "name", "period", "month", "date", "category", "x"];
    xAxisKey =
      commonXKeys.find((k) => sampleKeys.includes(k)) ??
      sampleKeys.find((k) => toFiniteNumber(workingData![0][k]) === null) ??
      sampleKeys[0];
  }

  // Normalize series definitions
  type NormalizedSeries = CartesianChartSpec["series"][number];
  const normalizedSeries: NormalizedSeries[] = [];

  if (rawSeriesList && rawSeriesList.length > 0) {
    rawSeriesList.forEach((item, idx) => {
      if (typeof item === "string" && item.trim()) {
        const rawStr = item.trim();
        const matchedKey =
          sampleKeys.find((k) => k === rawStr) ??
          sampleKeys.find((k) => k.toLowerCase() === rawStr.toLowerCase());
        if (matchedKey && matchedKey !== xAxisKey) {
          normalizedSeries.push({
            key: matchedKey,
            label: rawStr,
            color: DEFAULT_SERIES_COLORS[idx % DEFAULT_SERIES_COLORS.length],
          });
        }
        return;
      }

      if (!item || typeof item !== "object") return;
      const sr = item as Record<string, unknown>;
      const rawKey =
        typeof sr.key === "string" && sr.key.trim()
          ? sr.key.trim()
          : typeof sr.dataKey === "string" && sr.dataKey.trim()
          ? sr.dataKey.trim()
          : typeof sr.id === "string" && sr.id.trim()
          ? sr.id.trim()
          : `s${idx + 1}`;
      const rawLabel =
        typeof sr.label === "string" && sr.label.trim()
          ? sr.label.trim()
          : typeof sr.name === "string" && sr.name.trim()
          ? sr.name.trim()
          : rawKey;

      // Match key against actual data keys (exact, case-insensitive, or label match)
      const matchedKey =
        sampleKeys.find((k) => k === rawKey && k !== xAxisKey) ??
        sampleKeys.find((k) => k.toLowerCase() === rawKey.toLowerCase() && k !== xAxisKey) ??
        sampleKeys.find((k) => k.toLowerCase() === rawLabel.toLowerCase() && k !== xAxisKey);

      if (!matchedKey) return;

      const sType =
        sr.type === "line" || sr.type === "bar" ? sr.type : undefined;
      const yAxisId =
        sr.yAxisId === "left" || sr.yAxisId === "right" ? sr.yAxisId : undefined;
      const color =
        typeof sr.color === "string" && sr.color.trim()
          ? sr.color.trim()
          : DEFAULT_SERIES_COLORS[idx % DEFAULT_SERIES_COLORS.length];

      normalizedSeries.push({
        key: matchedKey,
        label: rawLabel,
        ...(sType ? { type: sType } : {}),
        ...(yAxisId ? { yAxisId } : {}),
        color,
      });
    });
  }

  // Fallback: infer series from numeric columns if series array was missing or mismatched
  if (normalizedSeries.length === 0) {
    const numericKeys = sampleKeys.filter(
      (k) => k !== xAxisKey && toFiniteNumber(workingData![0][k]) !== null
    );
    numericKeys.forEach((k, idx) => {
      normalizedSeries.push({
        key: k,
        label: k.replace(/[_-]+/g, " ").replace(/\b\w/g, (c) => c.toUpperCase()),
        color: DEFAULT_SERIES_COLORS[idx % DEFAULT_SERIES_COLORS.length],
      });
    });
  }

  if (normalizedSeries.length === 0) return undefined;

  // Coerce data rows and keep only series that have at least one valid number
  const cleanData: Array<Record<string, string | number>> = workingData.map((row, idx) => {
    const xVal = row[xAxisKey!];
    const cleanRow: Record<string, string | number> = {
      [xAxisKey!]:
        typeof xVal === "string" || typeof xVal === "number" ? xVal : String(idx + 1),
    };
    for (const s of normalizedSeries) {
      const num = toFiniteNumber(row[s.key]);
      if (num !== null) {
        cleanRow[s.key] = num;
      }
    }
    return cleanRow;
  });

  const validSeries = normalizedSeries.filter((s) =>
    cleanData.some((row) => typeof row[s.key] === "number")
  );
  if (validSeries.length === 0) return undefined;

  return {
    kind,
    title,
    ...(subtitle ? { subtitle } : {}),
    xAxisKey,
    yAxisUnit,
    ...(yAxisRightUnit ? { yAxisRightUnit } : {}),
    series: validSeries,
    data: cleanData,
    ...(sourceNote ? { sourceNote } : {}),
  };
}

/**
 * Normalizes a ChartSpec if it is a Cartesian chart, or returns non-Cartesian specs as-is.
 */
export function normalizeChartSpec(raw: unknown): ChartSpec | undefined {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return undefined;
  const obj = raw as Record<string, unknown>;
  const cartesian = normalizeCartesianChartSpec(raw);
  if (cartesian) return cartesian;
  if (
    obj.kind === "pie" ||
    obj.kind === "table" ||
    obj.kind === "process" ||
    obj.kind === "map" ||
    obj.kind === "composite"
  ) {
    return raw as ChartSpec;
  }
  return undefined;
}
