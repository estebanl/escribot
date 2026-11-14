/** Operator details shown on legal pages and in the footer. */
export function getSiteInfo() {
  return {
    operator: process.env.SITE_OPERATOR?.trim() || "the operator of FluentEscritor",
    supportEmail: process.env.SUPPORT_EMAIL?.trim() || null,
    jurisdiction: process.env.SITE_JURISDICTION?.trim() || null,
  };
}

export const TRADEMARK_NOTICE =
  "IELTS is a registered trademark of the British Council, IDP: IELTS Australia and Cambridge University Press & Assessment. FluentEscritor is an independent practice tool and is not affiliated with, endorsed by or approved by them. Band scores are AI estimates for practice, not official results.";
