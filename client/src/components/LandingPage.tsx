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
import { PLANS } from "@/lib/plans";
import { PublicHeader } from "@/components/PublicHeader";
import { SiteFooter } from "@/components/SiteFooter";

const FEATURES = [
  {
    icon: ClipboardCheck,
    title: "Examiner-style band scores",
    body: "Every essay is marked on the four official criteria in half-band steps, with highlighted sentences and a rewrite at your target band.",
  },
  {
    icon: BookOpen,
    title: "Coach Studio",
    body: "Check your Task 1 overview or Task 2 thesis in a five-minute outline step before you write, then redraft and watch the band move.",
  },
  {
    icon: Timer,
    title: "Timed exam practice",
    body: "Sit 20, 40 or 60-minute computer-based sessions with the on-screen tools you will have on test day.",
  },
  {
    icon: Languages,
    title: "First-language coaching",
    body: "Catches false friends and grammar habits carried over from your first language, such as Spanish actualmente → ‘actually’.",
  },
  {
    icon: Bookmark,
    title: "Personal error Vault",
    body: "Your recurring mistakes and stronger phrasings are saved automatically and come back as flashcards.",
  },
  {
    icon: Zap,
    title: "Five-minute drills",
    body: "Short targeted exercises on overviews, cohesion, vocabulary and hedging, pitched at your current level.",
  },
];

const STEPS = [
  { title: "Tell us your band", body: "Set your current and target band and your first language." },
  { title: "Write a task", body: "Pick from the prompt library, generate a new one, or upload a chart." },
  { title: "Get marked, then redraft", body: "See exactly what held the score back and fix it in the next draft." },
];

export function LandingPage({ deleted = false }: { deleted?: boolean }) {
  const free = PLANS.free;
  const pro = PLANS.pro;

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
              Get past Band 6.5 in IELTS Writing
            </h1>
            <p className="text-lg text-ink-secondary leading-relaxed max-w-xl">
              Write Task 1 and Task 2 essays, get detailed band-score feedback in under a minute,
              and turn every mistake into practice for the next draft.
            </p>
            <div className="flex flex-col sm:flex-row gap-3">
              <Link
                id="landing-signup-button"
                to="/signup"
                className="inline-flex items-center justify-center gap-2 px-5 py-3 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium transition"
              >
                Start free
                <ArrowRight className="w-4 h-4" />
              </Link>
              <Link
                to="/pricing"
                className="inline-flex items-center justify-center px-5 py-3 rounded-lg border border-line-strong hover:bg-subtle text-ink font-medium transition"
              >
                See pricing
              </Link>
            </div>
            <p className="text-sm text-ink-muted">
              Free plan: {free.evaluationsPerMonth} marked essays a month. No card needed.
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
              Everything between you and a higher band
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
              <h2 className="font-serif text-2xl font-semibold">Start on the Free plan</h2>
              <p className="text-ink-secondary">
                {free.evaluationsPerMonth} marked essays a month for free. Pro raises that to{" "}
                {pro.evaluationsPerMonth}, or bring your own AI key for no limits.
              </p>
            </div>
            <Link
              to="/signup"
              className="shrink-0 inline-flex items-center justify-center gap-2 px-5 py-3 rounded-lg bg-accent hover:bg-accent-hover text-accent-fg font-medium transition"
            >
              Create your account
              <ArrowRight className="w-4 h-4" />
            </Link>
          </div>
        </section>
      </main>

      <SiteFooter />
    </div>
  );
}
