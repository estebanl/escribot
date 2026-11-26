import { Link } from "react-router";
import {
  ArrowRight,
  BookOpen,
  Bookmark,
  ClipboardCheck,
  Languages,
  Timer,
  Zap,
} from "lucide-react";
import type { PlanCatalog } from "@/lib/plans";
import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";

const FEATURES = [
  {
    icon: ClipboardCheck,
    title: "Scored on the four official criteria",
    body: "Task Response, Coherence, Lexical Resource and Grammar, each in half-band steps, with the sentences that cost you marks and a rewrite at your target band.",
  },
  {
    icon: BookOpen,
    title: "Plan before you write",
    body: "Spend five minutes on your overview or thesis and get it checked before you start, then redraft and compare.",
  },
  {
    icon: Timer,
    title: "Practise under exam conditions",
    body: "20, 40 or 60-minute sessions on a computer-based exam screen, with the tools you’ll have on test day.",
  },
  {
    icon: Languages,
    title: "Coaching that knows your first language",
    body: "Spots false friends and habits carried over from your first language, like writing ‘actually’ when Spanish ‘actualmente’ means ‘currently’.",
  },
  {
    icon: Bookmark,
    title: "Your mistakes, turned into flashcards",
    body: "Recurring errors and better phrasings are saved for you and come back as flashcards until they stick.",
  },
  {
    icon: Zap,
    title: "Five-minute drills",
    body: "Quick exercises on overviews, cohesion, vocabulary and hedging, pitched at your level.",
  },
];

const STEPS = [
  { title: "Set your goal", body: "Your current band, your target and your first language. It takes a minute." },
  { title: "Write an essay", body: "Choose from the prompt library, generate a new prompt, or upload your own Task 1 chart." },
  { title: "Get marked, then redraft", body: "See what held the score back and fix it in the next draft." },
];

export function LandingPage({ plans, deleted = false }: { plans: PlanCatalog; deleted?: boolean }) {
  const free = plans.free;
  const pro = plans.pro;

  return (
    <div className="min-h-screen flex flex-col bg-canvas text-ink">
      <PublicHeader />

      <main className="flex-1">
        {deleted && (
          <div className="max-w-5xl mx-auto px-4 sm:px-6 pt-6">
            <p
              id="account-deleted-notice"
              role="status"
              className="px-4 py-3 rounded-lg border border-line-strong bg-subtle text-sm text-ink"
            >
              Your account and all of its data have been deleted.
            </p>
          </div>
        )}

        <section className="max-w-5xl mx-auto px-4 sm:px-6 pt-14 pb-16 grid lg:grid-cols-[1.2fr_1fr] gap-10 items-center">
          <div className="space-y-6">
            <p className="text-xs font-mono uppercase tracking-widest text-accent font-semibold">
              Academic IELTS Writing practice
            </p>
            <h1 className="font-serif text-4xl sm:text-5xl font-semibold tracking-tight leading-tight">
              Stuck at 6.5 in IELTS Writing? See exactly what’s holding you back.
            </h1>
            <p className="text-lg text-ink-secondary leading-relaxed max-w-xl">
              Write a Task 1 or Task 2 essay and get it marked on all four criteria in under a
              minute, with the weak sentences highlighted and rewritten. Then redraft and watch your
              band move.
            </p>
            <div className="flex flex-col sm:flex-row gap-3">
              <Link
                id="landing-signup-button"
                to="/signup"
                className="inline-flex items-center justify-center gap-2 px-5 py-3 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium transition"
              >
                Mark my first essay free
                <ArrowRight className="w-4 h-4" />
              </Link>
              <Link
                to="/pricing"
                className="inline-flex items-center justify-center px-5 py-3 rounded-lg border border-line-strong hover:bg-subtle text-ink font-medium transition"
              >
                Compare plans
              </Link>
            </div>
            <p className="text-sm text-ink-muted">
              {free.evaluationsPerMonth} free marked essays every month. No card needed.
            </p>
          </div>

          <figure
            aria-label="Example feedback"
            className="editorial-surface rounded-2xl p-6 space-y-4"
          >
            <figcaption className="text-xs font-mono uppercase tracking-widest text-ink-muted">
              Example feedback · Task 2, draft 2
            </figcaption>
            <div className="flex items-baseline justify-between">
              <span className="font-serif text-lg font-semibold">Overall band</span>
              <span className="font-serif text-4xl font-semibold text-accent">7.0</span>
            </div>
            <dl className="grid grid-cols-2 gap-3 text-sm">
              {[
                ["Task Response", "7.0"],
                ["Coherence & Cohesion", "6.5"],
                ["Lexical Resource", "7.5"],
                ["Grammar", "7.0"],
              ].map(([k, v]) => (
                <div key={k} className="rounded-lg bg-subtle px-3 py-2">
                  <dt className="text-ink-secondary text-xs">{k}</dt>
                  <dd className="font-mono font-medium">{v}</dd>
                </div>
              ))}
            </dl>
            <p className="text-sm leading-relaxed">
              <span className="line-through text-ink-muted">Actually, many cities</span>{" "}
              <span className="text-accent font-medium">At present, many cities</span> face
              severe housing shortages.
            </p>
            <p className="text-xs text-ink-secondary">
              ‘Actually’ is a false friend of Spanish ‘actualmente’ (currently).
            </p>
          </figure>
        </section>

        <section aria-labelledby="features-heading" className="bg-surface border-y border-line">
          <div className="max-w-5xl mx-auto px-4 sm:px-6 py-16 space-y-10">
            <h2 id="features-heading" className="font-serif text-3xl font-semibold text-center">
              What moves you up a band
            </h2>
            <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-6">
              {FEATURES.map(({ icon: Icon, title, body }) => (
                <div key={title} className="space-y-2">
                  <Icon className="w-5 h-5 text-accent" />
                  <h3 className="font-semibold">{title}</h3>
                  <p className="text-sm text-ink-secondary leading-relaxed">{body}</p>
                </div>
              ))}
            </div>
          </div>
        </section>

        <section aria-labelledby="steps-heading" className="max-w-5xl mx-auto px-4 sm:px-6 py-16 space-y-8">
          <h2 id="steps-heading" className="font-serif text-3xl font-semibold text-center">
            How it works
          </h2>
          <ol className="grid md:grid-cols-3 gap-6">
            {STEPS.map((step, i) => (
              <li key={step.title} className="editorial-surface rounded-2xl p-6 space-y-2">
                <span className="font-mono text-sm text-accent">0{i + 1}</span>
                <h3 className="font-semibold">{step.title}</h3>
                <p className="text-sm text-ink-secondary">{step.body}</p>
              </li>
            ))}
          </ol>
        </section>

        <section className="max-w-5xl mx-auto px-4 sm:px-6 pb-20">
          <div className="editorial-surface rounded-2xl p-8 sm:p-10 flex flex-col md:flex-row md:items-center justify-between gap-6">
            <div className="space-y-2">
              <h2 className="font-serif text-2xl font-semibold">Find out your band today</h2>
              <p className="text-ink-secondary">
                {free.evaluationsPerMonth} marked essays a month, free. Pro gives you{" "}
                {pro.evaluationsPerMonth}, or bring your own AI key and write without limits.
              </p>
            </div>
            <Link
              to="/signup"
              className="shrink-0 inline-flex items-center justify-center gap-2 px-5 py-3 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium transition"
            >
              Start writing free
              <ArrowRight className="w-4 h-4" />
            </Link>
          </div>
        </section>
      </main>

      <SiteFooter />
    </div>
  );
}
