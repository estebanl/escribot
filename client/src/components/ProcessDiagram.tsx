import { useId, useLayoutEffect, useMemo, useRef, useState, type SVGProps } from "react";
import type { ChartSpec } from "@/lib/types";

type ProcessSpec = Extract<ChartSpec, { kind: "process" }>;
type Step = ProcessSpec["steps"][number];

/*
 * A Task 1 process diagram drawn as one SVG image: the title, a numbered box per stage and
 * arrows between them. A cyclic process is laid out clockwise around a ring (or down one
 * column on narrow screens) with an arrow from the last stage back to the first.
 */

const SANS = "Inter, ui-sans-serif, system-ui, sans-serif";
const MONO = '"IBM Plex Mono", ui-monospace, monospace';

const INK = "#0f172a";
const INK_SOFT = "#475569";
const LINE = "#94a3b8";
const ACCENT = "#16656e";

const TITLE = { size: 16, lh: 21 };
const SUBTITLE = { size: 12.5, lh: 17 };
const NAME = { size: 14, lh: 18 };
const DETAIL = { size: 12.5, lh: 17 };
const BADGE = { size: 10.5, h: 20 };
const PAD = 14;
const NUM_R = 13;
const NAME_X = PAD + NUM_R * 2 + 10;

/** Below this container width the stages are drawn in one column. */
const NARROW_BELOW = 520;

/* ---------- text measuring ---------- */

let measureCtx: CanvasRenderingContext2D | null | undefined;

function textWidth(text: string, size: number, family: string, bold = false): number {
  if (measureCtx === undefined) {
    measureCtx = typeof document === "undefined" ? null : document.createElement("canvas").getContext("2d");
  }
  if (!measureCtx) return text.length * size * (bold ? 0.6 : 0.55);
  measureCtx.font = `${bold ? 700 : 400} ${size}px ${family}`;
  // A little slack, in case the web font loads after measuring and is wider than the fallback.
  return measureCtx.measureText(text).width * 1.04;
}

function wrap(text: string, maxWidth: number, size: number, family = SANS, bold = false): string[] {
  const lines: string[] = [];
  let line = "";
  for (const word of text.split(/\s+/).filter(Boolean)) {
    const next = line ? `${line} ${word}` : word;
    if (line && textWidth(next, size, family, bold) > maxWidth) {
      lines.push(line);
      line = word;
    } else {
      line = next;
    }
  }
  if (line) lines.push(line);
  return lines;
}

/* ---------- layout ---------- */

interface Box {
  step: Step;
  x: number;
  y: number;
  w: number;
  h: number;
  nameLines: string[];
  detailLines: string[];
}

interface Point {
  x: number;
  y: number;
}

interface Layout {
  width: number;
  height: number;
  titleLines: string[];
  subtitleLines: string[];
  boxes: Box[];
  arrows: Point[][];
  cycleMark?: Point;
}

function boxText(step: Step, w: number) {
  const nameLines = wrap(step.stageName, w - NAME_X - PAD, NAME.size, SANS, true);
  const detailLines = wrap(step.detail, w - PAD * 2, DETAIL.size);
  const header = Math.max(NUM_R * 2, nameLines.length * NAME.lh);
  const h =
    PAD + header + 8 + detailLines.length * DETAIL.lh + (step.badge ? 10 + BADGE.h : 0) + PAD;
  return { nameLines, detailLines, h };
}

const center = (b: Box): Point => ({ x: b.x + b.w / 2, y: b.y + b.h / 2 });

/** Where a line leaving box `b` towards `toward` (straight across or down) meets its edge. */
function edge(b: Box, toward: Point, gap = 6): Point {
  const c = center(b);
  const dx = toward.x - c.x;
  const dy = toward.y - c.y;
  if (Math.abs(dx) >= Math.abs(dy)) {
    return { x: c.x + Math.sign(dx) * (b.w / 2 + gap), y: c.y };
  }
  return { x: c.x, y: c.y + Math.sign(dy) * (b.h / 2 + gap) };
}

/** Clockwise cells around the edge of a cols × rows grid, starting top-left. */
function ringCells(cols: number, rows: number): Array<[number, number]> {
  const cells: Array<[number, number]> = [];
  for (let c = 0; c < cols; c++) cells.push([c, 0]);
  for (let r = 1; r < rows; r++) cells.push([cols - 1, r]);
  for (let c = cols - 2; c >= 0; c--) cells.push([c, rows - 1]);
  for (let r = rows - 2; r >= 1; r--) cells.push([0, r]);
  return cells;
}

function layoutDiagram(spec: ProcessSpec, narrow: boolean): Layout {
  const steps = [...spec.steps].sort((a, b) => a.stepNumber - b.stepNumber);
  const n = steps.length;
  const cyclic = Boolean(spec.isCyclic) && n > 1;

  const width = narrow ? 360 : 900;
  const margin = narrow ? 12 : 24;
  const titleLines = wrap(spec.title, width - margin * 2, TITLE.size, SANS, true);
  const subtitleLines = spec.subtitle ? wrap(spec.subtitle, width - margin * 2, SUBTITLE.size) : [];
  const top =
    margin + titleLines.length * TITLE.lh + (subtitleLines.length ? 4 + subtitleLines.length * SUBTITLE.lh : 0) + 20;

  // Grid cell for each step, in order.
  let cols: number;
  let rows: number;
  let cells: Array<[number, number]>;
  if (narrow) {
    cols = 1;
    rows = n;
    cells = steps.map((_, i) => [0, i]);
  } else if (cyclic) {
    cols = 2;
    rows = 2;
    while (2 * cols + 2 * rows - 4 < n) {
      if (cols === rows) cols++;
      else rows++;
    }
    cells = ringCells(cols, rows);
  } else {
    cols = n <= 3 ? n : n === 4 ? 2 : 3;
    rows = Math.ceil(n / cols);
    // Back and forth along the rows, so each step sits beside the one before it.
    cells = steps.map((_, i) => {
      const r = Math.floor(i / cols);
      const c = i % cols;
      return [r % 2 === 0 ? c : cols - 1 - c, r];
    });
  }

  const colGap = 56;
  const rowGap = narrow ? 30 : 44;
  const gutter = narrow && cyclic ? 30 : 0;
  const cellW = (width - margin * 2 - gutter - colGap * (cols - 1)) / cols;
  const texts = steps.map((s) => boxText(s, cellW));
  const cellH = Math.max(...texts.map((t) => t.h));

  const cellBox = (c: number, r: number, i: number): Box => ({
    step: steps[i],
    x: margin + c * (cellW + colGap),
    y: top + r * (cellH + rowGap),
    w: cellW,
    h: cellH,
    nameLines: texts[i].nameLines,
    detailLines: texts[i].detailLines,
  });
  const boxes = steps.map((_, i) => cellBox(cells[i][0], cells[i][1], i));

  const arrows: Point[][] = [];
  for (let i = 0; i < n - 1; i++) {
    const to = center(boxes[i + 1]);
    const from = center(boxes[i]);
    arrows.push([edge(boxes[i], to), edge(boxes[i + 1], from)]);
  }

  let cycleMark: Point | undefined;
  if (cyclic) {
    const first = boxes[0];
    const last = boxes[n - 1];
    if (narrow) {
      // Out of the last box's right side, up the gutter, into the first box's right side.
      const gx = first.x + first.w + gutter - 6;
      const lc = center(last);
      const fc = center(first);
      arrows.push([
        { x: last.x + last.w + 6, y: lc.y },
        { x: gx, y: lc.y },
        { x: gx, y: fc.y },
        { x: first.x + first.w + 6, y: fc.y },
      ]);
    } else {
      // Through the centres of the ring cells left empty, back to the first box.
      const via = cells.slice(n).map(([c, r]) => center(cellBox(c, r, 0)));
      const route = [center(last), ...via, center(first)];
      arrows.push([edge(last, route[1]), ...via, edge(first, route[route.length - 2])]);
      if (cols >= 3 && rows >= 3) {
        const a = cellBox(1, 1, 0);
        const b = cellBox(cols - 2, rows - 2, 0);
        cycleMark = { x: (a.x + b.x + b.w) / 2, y: (a.y + b.y + b.h) / 2 };
      }
    }
  }

  const height = top + rows * cellH + (rows - 1) * rowGap + margin;
  return { width, height, titleLines, subtitleLines, boxes, arrows, cycleMark };
}

/* ---------- drawing ---------- */

function Lines({
  lines,
  x,
  y,
  lh,
  ...props
}: { lines: string[]; x: number; y: number; lh: number } & SVGProps<SVGTextElement>) {
  return (
    <text x={x} y={y + lh * 0.74} {...props}>
      {lines.map((line, i) => (
        <tspan key={i} x={x} dy={i === 0 ? 0 : lh}>
          {line}
        </tspan>
      ))}
    </text>
  );
}

function StageBox({ box }: { box: Box }) {
  const { step, x, y, w, h, nameLines, detailLines } = box;
  const header = Math.max(NUM_R * 2, nameLines.length * NAME.lh);
  const nameTop = y + PAD + Math.max(0, (NUM_R * 2 - nameLines.length * NAME.lh) / 2);
  const badgeW = step.badge ? textWidth(step.badge, BADGE.size, MONO) + 16 : 0;
  return (
    <g>
      <rect x={x} y={y} width={w} height={h} rx={10} fill="#f8fafc" stroke={LINE} strokeWidth={1.25} />
      <circle cx={x + PAD + NUM_R} cy={y + PAD + NUM_R} r={NUM_R} fill={ACCENT} />
      <text
        x={x + PAD + NUM_R}
        y={y + PAD + NUM_R}
        textAnchor="middle"
        dominantBaseline="central"
        fill="#fff"
        fontSize={13}
        fontWeight={700}
      >
        {step.stepNumber}
      </text>
      <Lines lines={nameLines} x={x + NAME_X} y={nameTop} lh={NAME.lh} fill={INK} fontSize={NAME.size} fontWeight={700} />
      <Lines
        lines={detailLines}
        x={x + PAD}
        y={y + PAD + header + 8}
        lh={DETAIL.lh}
        fill={INK_SOFT}
        fontSize={DETAIL.size}
      />
      {step.badge && (
        <g>
          <rect x={x + PAD} y={y + h - PAD - BADGE.h} width={badgeW} height={BADGE.h} rx={4} fill="#fef3c7" />
          <text
            x={x + PAD + 8}
            y={y + h - PAD - BADGE.h / 2}
            dominantBaseline="central"
            fill="#78350f"
            fontSize={BADGE.size}
            fontFamily={MONO}
          >
            {step.badge}
          </text>
        </g>
      )}
    </g>
  );
}

function CycleMark({ at, arrowId }: { at: Point; arrowId: string }) {
  const r = 28;
  const point = (deg: number) => {
    const rad = (deg * Math.PI) / 180;
    return `${at.x + r * Math.cos(rad)} ${at.y - 12 + r * Math.sin(rad)}`;
  };
  return (
    <g>
      <path
        d={`M ${point(-70)} A ${r} ${r} 0 1 1 ${point(-110)}`}
        fill="none"
        stroke={ACCENT}
        strokeWidth={2.5}
        markerEnd={`url(#${arrowId})`}
      />
      <text x={at.x} y={at.y + r + 12} textAnchor="middle" fill={ACCENT} fontSize={12} fontWeight={700}>
        Continuous cycle
      </text>
    </g>
  );
}

export function ProcessDiagram({ spec }: { spec: ProcessSpec }) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const [narrow, setNarrow] = useState(false);
  const arrowId = `process-arrow-${useId().replace(/:/g, "")}`;
  const titleId = `${arrowId}-title`;
  const descId = `${arrowId}-desc`;

  useLayoutEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => {
      setNarrow(entry.contentRect.width < NARROW_BELOW);
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const layout = useMemo(() => layoutDiagram(spec, narrow), [spec, narrow]);
  if (layout.boxes.length === 0) return null;

  const description = [
    spec.subtitle,
    ...layout.boxes.map((b) => `Stage ${b.step.stepNumber}: ${b.step.stageName}. ${b.step.detail}`),
    spec.isCyclic ? "The last stage leads back to the first." : undefined,
  ]
    .filter(Boolean)
    .join(" ");
  const margin = narrow ? 12 : 24;

  return (
    <div ref={wrapRef}>
      <svg
        viewBox={`0 0 ${layout.width} ${layout.height}`}
        width="100%"
        role="img"
        aria-labelledby={`${titleId} ${descId}`}
        fontFamily={SANS}
        className="block h-auto"
      >
        <title id={titleId}>{spec.title}</title>
        <desc id={descId}>{description}</desc>
        <defs>
          <marker id={arrowId} viewBox="0 0 10 10" refX={8} refY={5} markerWidth={7} markerHeight={7} orient="auto-start-reverse">
            <path d="M 0 0 L 10 5 L 0 10 z" fill={ACCENT} />
          </marker>
        </defs>
        <rect width={layout.width} height={layout.height} fill="#fff" />
        <Lines
          lines={layout.titleLines}
          x={margin}
          y={margin}
          lh={TITLE.lh}
          fill={INK}
          fontSize={TITLE.size}
          fontWeight={700}
        />
        {layout.subtitleLines.length > 0 && (
          <Lines
            lines={layout.subtitleLines}
            x={margin}
            y={margin + layout.titleLines.length * TITLE.lh + 4}
            lh={SUBTITLE.lh}
            fill={INK_SOFT}
            fontSize={SUBTITLE.size}
          />
        )}
        {layout.arrows.map((points, i) => (
          <polyline
            key={i}
            points={points.map((p) => `${p.x},${p.y}`).join(" ")}
            fill="none"
            stroke={ACCENT}
            strokeWidth={2}
            strokeLinejoin="round"
            markerEnd={`url(#${arrowId})`}
          />
        ))}
        {layout.boxes.map((box) => (
          <StageBox key={box.step.stepNumber} box={box} />
        ))}
        {layout.cycleMark && <CycleMark at={layout.cycleMark} arrowId={arrowId} />}
      </svg>
    </div>
  );
}
