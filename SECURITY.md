# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue or pull request.

Use GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**. Only the maintainer can see the report.

Please include:

- what the problem is and what an attacker could do with it,
- the steps or request needed to reproduce it,
- the commit or deployment you tested against.

Do not include real user data, and do not test against accounts that are not yours.

## What to expect

This is a personal project maintained by one person. You should get a reply within a week. Confirmed problems are fixed on `main`, and the fix is credited to you in the advisory unless you prefer otherwise.

## Scope

In scope: the code in this repository and the deployment built from it, in particular anything that lets one account read or change another account's data, bypasses sign-in or usage limits, or exposes stored API keys.

Out of scope: findings that need a compromised device or account, missing hardening headers with no demonstrated impact, and volumetric denial of service.

## Secrets in the repository

No credential should ever be committed here. Every pull request is scanned with gitleaks across the full history. If you find a secret in the code or the history, report it the same way so it can be rotated.
