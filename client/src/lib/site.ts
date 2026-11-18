/** Operator details shown on legal pages, from the server's public config (GET /api/config). */
export interface SiteInfo {
  operator: string;
  supportEmail: string | null;
  jurisdiction: string | null;
}

export const TRADEMARK_NOTICE =
  "IELTS is a registered trademark of the British Council, IDP: IELTS Australia and Cambridge University Press & Assessment. FluentEscritor is an independent practice tool and is not affiliated with, endorsed by or approved by them. Band scores are AI estimates for practice, not official results.";
