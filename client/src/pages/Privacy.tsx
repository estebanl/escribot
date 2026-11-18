import { Link, useLoaderData } from "react-router";
import { LegalPage } from "@/components/LegalPage";
import { loadJson } from "@/lib/api";
import type { SiteInfo } from "@/lib/site";

export async function loader({ request }: { request: Request }) {
  return loadJson<{ site: SiteInfo }>("/api/config", request);
}

export function PrivacyPage() {
  const { site } = useLoaderData<typeof loader>();
  const contact = site.supportEmail ? (
    <a href={`mailto:${site.supportEmail}`}>{site.supportEmail}</a>
  ) : (
    "the contact details published on this site"
  );

  return (
    <LegalPage title="Privacy Policy" updated="November 14, 2026">
      <p>
        FluentEscritor is operated by {site.operator} (“we”). This policy explains what we
        collect when you use FluentEscritor, why, who we share it with, and the choices you have.
      </p>

      <h2>What we collect</h2>
      <ul>
        <li>
          <strong>Account details:</strong> your name, email address, a hashed password (or your
          Google account identifier if you sign in with Google), and the time your account was
          created.
        </li>
        <li>
          <strong>Your study content:</strong> essays and drafts, outlines, notes, prompts you
          create or upload (including chart images), examiner feedback, Vault items and drill
          answers.
        </li>
        <li>
          <strong>Settings:</strong> current and target band, exam date, first language and AI
          preferences. If you save your own AI provider API key it is stored encrypted and used
          only to make requests on your behalf.
        </li>
        <li>
          <strong>Usage records:</strong> which AI features you used, when, token counts and
          cost, to apply plan limits and show your usage.
        </li>
        <li>
          <strong>Billing:</strong> if you subscribe, our payment provider gives us your
          subscription status and renewal date. We never see or store your card details.
        </li>
        <li>
          <strong>Technical data:</strong> IP address and browser user agent for each signed-in
          session, IP-based counters used to prevent abuse, and error reports when something
          goes wrong.
        </li>
      </ul>

      <h2>How we use it</h2>
      <ul>
        <li>To provide the service: store your work, mark your essays and generate practice.</li>
        <li>To run your account: sign-in, email verification, password reset, billing.</li>
        <li>To keep the service safe and working: rate limits, abuse prevention, fixing errors.</li>
      </ul>
      <p>
        We do not sell your data, show advertising, or use your essays to train AI models. We
        only process your data where it is needed to provide the service you asked for
        (performance of a contract) or for the legitimate interest of keeping the service secure.
      </p>

      <h2>Who receives your data</h2>
      <p>We use these service providers, each only for the purpose listed:</p>
      <ul>
        <li>
          <strong>AI model providers</strong> (through OpenRouter, or directly with DeepSeek,
          Google, OpenAI or Anthropic): the text of the essay, prompt or passage you ask about,
          and uploaded chart images, are sent to generate feedback. Requests through OpenRouter
          are only routed to model providers that do not store or train on the data. If you use
          your own API key, your agreement with that provider applies instead.
        </li>
        <li>
          <strong>Hosting and storage</strong> (Vercel, Turso): run the application and store
          your account, content and uploaded images.
        </li>
        <li>
          <strong>Payments</strong> (Stripe): handles checkout, subscriptions and invoices.
        </li>
        <li>
          <strong>Email</strong> (Resend): sends verification and password-reset emails.
        </li>
        <li>
          <strong>Google</strong>: only if you choose “Continue with Google”.
        </li>
      </ul>
      <p>
        Some of these providers process data outside your country, including in the United
        States. Where required, transfers rely on the providers’ standard contractual clauses.
      </p>

      <h2>Cookies</h2>
      <p>
        We use one essential cookie to keep you signed in. Your light/dark theme choice is kept
        in your browser’s local storage. We use no analytics or advertising cookies.
      </p>

      <h2>How long we keep it</h2>
      <p>
        We keep your data while your account exists. When you delete your account, your
        account, content, images, settings and usage records are erased immediately and your
        subscription is cancelled. Copies in our providers’ backups expire on their normal
        backup cycle. Payment records are kept by Stripe as required for accounting and tax.
      </p>

      <h2>Your rights</h2>
      <ul>
        <li>
          <strong>Access and portability:</strong> download everything we hold about you from{" "}
          <Link to="/settings">Settings → Account &amp; Privacy</Link>.
        </li>
        <li>
          <strong>Deletion:</strong> delete your account at any time from the same place.
        </li>
        <li>
          <strong>Correction:</strong> edit your details and settings in the app, or contact us.
        </li>
        <li>
          <strong>Objection and complaints:</strong> contact us at {contact}. You can also
          complain to your local data protection authority.
        </li>
      </ul>

      <h2>Children</h2>
      <p>
        FluentEscritor is intended for people aged 16 or older. If you are younger, you need
        permission from a parent or guardian.
      </p>

      <h2>Changes</h2>
      <p>
        If we change this policy in a way that matters, we will tell you in the app or by email
        before the change takes effect.
      </p>

      <h2>Contact</h2>
      <p>Questions about privacy: {contact}.</p>
    </LegalPage>
  );
}
