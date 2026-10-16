# 0021: Azure Container Apps Environment as the deployment target

**Status:** Accepted for deployment direction; implementation and hosting
compatibility validation remain pending.

## Context

The application currently runs through a local Aspire AppHost with development
services and credentials. The user selected Azure Container Apps Environment as
the desired deployment target, rather than Azure App Service.

The application needs an API, a Durable Functions host, and execution tooling
that can download repositories and generate review branches. The Vue website is
a sample/reference client, not a required production frontend. Repository
execution also needs stronger isolation before production.

## Decision

Target Azure Container Apps Environment for application compute. Plan distinct
API and workflow containers rather than combining all processes into a
single container. Validate the supported Functions hosting/deployment model and
its compatibility with this application before committing infrastructure.

Keep PostgreSQL and durable workflow state outside disposable container
filesystems. Do not deploy the development AppHost or its emulators as production
services. Repository checkouts are per-run workspaces, not durable workflow state.

This decision selects the platform direction only. It does not approve public
ingress, a particular identity provider, a hosting SKU, scaling settings, or
running untrusted generated commands alongside orchestration credentials.
Separate isolated implementation jobs remain production-readiness work; whether
to use Container Apps Jobs or another isolation mechanism is still open.

## Consequences

- Deployment preparation should focus on container images and Container Apps
  infrastructure, not App Service configuration.
- Clarification after the initial platform decision: Vue is a sample client.
  Production API packaging should not require it and should omit sample assets
  by default. Explicit inclusion is optional; Vite remains development-only.
- Workflow packaging must include a compatible Functions host, Git, Copilot
  runtime, and the toolchains needed for approved repositories.
- Managed data services, secret handling, ingress/egress, workload identities,
  resource limits, and restart/scale behavior still need explicit design and
  staging validation.
- A shared Container Apps Environment is not, by itself, an execution sandbox.
- Production authentication and authorization remain release blockers. Selecting
  a deployment platform does not remove the API's development-only boundary.
- No Azure resources are provisioned by this record.
