import { Link, useLoaderData } from "react-router";
import { LegalPage } from "@/components/LegalPage";
import { loadJson } from "@/lib/api";
import { TRADEMARK_NOTICE, type SiteInfo } from "@/lib/site";

export async function loader({ request }: { request: Request }) {
  return loadJson<{ site: SiteInfo }>("/api/config", request);
}

export function TermsPage() {
  const { site } = useLoaderData<typeof loader>();
  const contact = site.supportEmail ? (
    <a href={`mailto:${site.supportEmail}`}>{site.supportEmail}</a>
  ) : (
    "the contact details published on this site"
  );

  return (
    <LegalPage title="Terms of Service" updated="November 14, 2026">
      <p>
        These terms are an agreement between you and {site.operator} (“we”) for your use of
        FluentEscritor. By creating an account you accept them.
      </p>

      <h2>The service</h2>
      <p>
        FluentEscritor is a practice tool for Academic IELTS Writing. It uses AI models to
        estimate band scores and suggest improvements. {TRADEMARK_NOTICE} Feedback can be wrong
        or incomplete; use it as practice guidance, not as a prediction of your test result.
      </p>

      <h2>Your account</h2>
      <ul>
        <li>You must be at least 16, or have permission from a parent or guardian.</li>
        <li>Give accurate details and keep your password secure; you are responsible for activity on your account.</li>
        <li>One person per account. Creating several accounts to get around plan limits is not allowed.</li>
      </ul>

      <h2>Your content</h2>
      <p>
        You keep ownership of the essays, notes and images you add. You give us permission to
        store and process them, including sending them to AI providers, only to provide the
        service to you. See the <Link to="/privacy">Privacy Policy</Link>.
      </p>
      <p>
        Only upload material you have the right to use. Do not upload content that is unlawful,
        infringes someone else’s rights, or contains other people’s personal data without their
        permission.
      </p>

      <h2>Acceptable use</h2>
      <ul>
        <li>Do not try to access other users’ data or bypass security, rate limits or plan limits.</li>
        <li>Do not use the service to generate content for others to submit as their own work in an assessment.</li>
        <li>Do not resell or automate access to the service without our written permission.</li>
      </ul>

      <h2>Plans and payment</h2>
      <ul>
        <li>The Free plan includes a monthly allowance that we may change with notice.</li>
        <li>
          Pro is a subscription billed in advance each month or year through Stripe. It renews
          automatically until you cancel.
        </li>
        <li>
          You can cancel at any time from Settings → Manage billing. Pro stays active until the
          end of the period you paid for. Payments are not refunded except where the law
          requires it.
        </li>
        <li>We will tell you before any price change applies to your subscription.</li>
        <li>If you use your own AI provider API key, that provider bills you directly.</li>
      </ul>

      <h2>Ending your account</h2>
      <p>
        You can delete your account at any time in Settings, which cancels any subscription
        immediately. We may suspend or close accounts that break these terms; where reasonable
        we will warn you first and refund any unused prepaid period if the closure is not your
        fault.
      </p>

      <h2>Disclaimers and liability</h2>
      <p>
        The service is provided “as is”. We work to keep it available and accurate but do not
        guarantee that it will be uninterrupted, error-free, or that using it will improve your
        score. To the extent the law allows, our total liability to you is limited to the
        amount you paid us in the 12 months before the claim. Nothing in these terms limits
        rights you have under consumer protection law that cannot be excluded.
      </p>

      <h2>Changes</h2>
      <p>
        We may update these terms. If a change matters, we will tell you in the app or by email
        before it takes effect. Continuing to use the service after that means you accept the
        new terms.
      </p>

      <h2>Law and contact</h2>
      <p>
        These terms are governed by the laws of{" "}
        {site.jurisdiction ?? "the country where the operator is established"}, without
        affecting mandatory consumer rights in your country of residence. Contact: {contact}.
      </p>
    </LegalPage>
  );
}
