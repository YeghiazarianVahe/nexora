# Release security: transport and helper signing

## Unchanged production posture

The pilot uses HTTP.sys Windows authentication on HTTP port 5090. Remediation changes no bindings, URLs, certificates, firewall rules, AD configuration or deployed helper/trust material. CredentialOps stays false and Diagnostics stays true. Security-owner acceptance of HTTP and endpoint-protection acceptance of unsigned code remain open.

## Practical TLS proposal (inactive)

Prefer a direct HTTPS binding on the existing HTTP.sys architecture, using an enterprise-issued server-authentication certificate with SANs for approved DNS names, trusted by clients. Use the existing machine hostname where possible to preserve machine-account Kerberos behavior. IP access generally negotiates NTLM; TLS by IP requires an IP SAN. A new DNS alias may require SPN/enterprise browser policy review; do not promise Kerberos with no AD implications for every alias.

On a disposable host: provision certificate/private-key access to the approved identity, reserve the reviewed HTTPS URL, configure the HTTP.sys SSL certificate binding and Domain firewall scope, set candidate Nexora URL/public helper URL explicitly, then test browser and helper Windows SSO, CSRF/origin, WebSocket upgrades, reconnection and certificate trust. The candidate now uses request-appropriate secure antiforgery cookies and ws/wss CSP targets without enabling HTTPS or redirecting existing HTTP. Do not trust forwarded identity headers from arbitrary clients.

IIS/reverse-proxy deployment is not a drop-in front end for this HTTP.sys host. Microsoft's IIS hosting documentation explicitly distinguishes HTTP.sys from the IIS/ANCM/Kestrel configuration. If a proxy is chosen, design Windows authentication termination, connection affinity, delegation/double-hop, trusted forwarders and helper routes explicitly; this is an architecture/operations decision, not silently implemented here. See [Windows hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/).

Acceptance requires approved certificate ownership/renewal, hostname/SPN and intranet policies, rollback bindings, helper URL compatibility, and real enterprise browser/identity checks. No certificate was issued or bound in remediation.

## Authenticode and pinned helper trust

Trust stamping adds public-key bytes to the executable. Sign **after** provisioning the server's public trust stamp. Mutating signed image/overlay data can invalidate Authenticode; the certificate table and specified PE header fields have special hash exclusions, not a general permission to append arbitrary mutable trust data. See Microsoft's [PE format](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format) and [executable signature model](https://learn.microsoft.com/en-us/windows/win32/secbp/understanding-pe-signatures).

Candidate code can locate the existing trust stamp immediately before an Authenticode certificate table, including alignment padding. It refuses to restamp an executable carrying a certificate table. A correctly pinned signed helper download preserves its exact bytes; a mismatched/missing trust stamp returns an actionable 503 instead of modifying the file. Existing unsigned stamped helpers remain compatible. Certificate-table detection is not cryptographic signature validation.

Authorized workflow: build the unstamped helper, obtain **only the approved public server trust key**, stamp a staging copy, verify its fingerprint, sign it through the organization's HSM/signing service or approved certificate store, timestamp it, then verify `signtool verify /pa /all /v` and `Get-AuthenticodeSignature`, run the signed helper on a disposable endpoint, and compare pre/post-download SHA-256 hashes. Repeat verification under deployed WDAC/AppLocker/Defender/SentinelOne policy. The private job-signing key must never be exported for this workflow.

No signing credentials were placed in source, no trusted signature was fabricated, and no real certificate-backed signing validation was performed. Unit tests use synthetic PE table layouts to test parsing/no-mutation behavior only. The candidate remains unsigned and is not approved for enterprise distribution until the workflow is executed and accepted.

## Privileged execution and host input

Preserve server-side role/catalog/confirmation/protected-target checks and helper signature, replay, SID binding and pinned collector hashes. SYSTEM jobs are fixed or approved-hash scripts prepared in protected directories; interactive helpers run as signed-in operators. Validate paths and refuse reparse points/untrusted owners. Telemetry upgrades must not write through existing linked scripts, replace unrelated tasks, or grant ordinary users parent-directory write rights. Local task install/remove testing is a separate VM gate; no task changes were applied to production.

Disk refresh failure responses now use a generic actionable message and correlation reference; detailed exceptions belong in protected structured logs. Per-SID HTTP action request limits use the exact configured validated count and do not apply to passive telemetry/hubs. Fake-identity role/revocation and hostile-input tests are isolated regression evidence, not a claim of a live enterprise penetration test.
