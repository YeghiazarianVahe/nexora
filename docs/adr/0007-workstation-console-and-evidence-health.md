# ADR-0007: Workstation console and evidence-based drive health

Status: Implemented locally, pending real Windows validation. Supersedes the score floors, broad vendor-life fallback, and HDS-compatible presentation language of ADR-0004/0005. Does not alter helper trust or authorization architecture.

## Decision

Keep the existing durable workstation routes and helper/job/audit flow. Put all eleven sections in the requested console order. Overview uses stored monitoring history; Hardware retains the existing bounded inventory cache and explicit refresh. Storage shows physical health separately from logical volume capacity.

Use an independent version-5 health heuristic with explicit confidence, availability and freshness. Remove the positive minimum health floor and ADATA uplift. Unsupported attributes and a generic Healthy flag cannot establish remaining life. Document the calculation and limits in [storage-health-v5.md](../storage-health-v5.md).

Extend the existing diagnostics profile/rules/collector pipeline with focused symptom profiles, not a parallel execution path or an LLM claim. Missing/null prerequisite evidence produces a not-evaluated rule and an incomplete result. Fixes remain operator-reviewed catalog actions or manual guidance.

Capture a job's workstation origin when it starts. Persist only validated local return paths, recognized action categories, and bounded search/risk filters in the job URL. Direct global jobs return to Jobs; scoped direct jobs return to their workstation.

Hide system, service, default and protected built-in profiles from the management list. Keep server-side deletion checks and the helper's independent execution-time guard, typed confirmations, audit, and token flow. Generic process Start/Restart is unavailable because inventory does not supply a trusted launch definition; managed services retain those controls.

## Consequences

Previously optimistic percentages can become Unavailable or lower. Confidence is evidence coverage, not an accuracy promise. Data collection frequency is unchanged. The new evidence collector runs only through an explicitly requested diagnostic job and has an outer helper deadline plus bounded CIM operations.

The implementation is verified locally with mocks/fixtures and isolated browser tests. No deployment, AD change, live workstation action, helper trust change, or destructive database migration is part of this decision.
