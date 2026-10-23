# ADR-0002: HTTP.sys with Windows Integrated Auth over plain HTTP

Status: Accepted (seeded from design history — verify against code)

## Context
SSO is needed in Chrome, Edge and Firefox. No certificates are available for the pilot, and IIS is not used.

## Decision
ASP.NET Core hosted on HTTP.sys with Negotiate authentication on `http://+:5080/`. The URL ACL and Domain-profile firewall rule are created by `nexora.ps1 -Action Install`.

## Consequences
- Credentials are sent ambiently → state-changing endpoints need CSRF protection (antiforgery token / custom header).
- No credential-handling features until HTTPS: `Features:CredentialOps = false`, enforced server-side.
- Moving to HTTPS later is a new ADR that also revisits C7.

## Constraints honored
C1, C2, C7.
