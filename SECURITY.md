# Security policy

## Supported versions

The current `main` branch and newest CI prerelease receive fixes during bootstrap. Older CI prereleases are not maintained; there is no stable release yet.

## Report a vulnerability

Use [GitHub private vulnerability reporting](https://github.com/ArcForges/ArcScope/security/advisories/new). Include the affected commit/version, platform, reproduction and impact. Do not publish credentials, exploit details or personal data in a public issue. If private reporting is unavailable, open an issue requesting a private contact without sensitive details.

The public Hello endpoint is unauthenticated and bounded by Cloud policy. The desktop application contains no service credentials and preserves TLS certificate validation. Repository security checks and native tests do not replace security review of future authentication, AI, permissions or storage features.

Maintainers assess reports and coordinate disclosure with the reporter. There is currently no guaranteed response SLA.
