# 0008: Route workflow calls through the Functions HTTP host

- **Status:** Accepted
- **Date:** 2026-10-13

## Context

The API delivers persisted workflow intents to Azure Functions through Aspire
service discovery. The Functions project uses the isolated worker model with
ASP.NET Core HTTP integration. Its worker listener expects Functions host
correlation headers and is not the public trigger endpoint.

The AppHost referenced the Functions project without configuring its HTTP
endpoint. In a clean local Functions/Azurite start, Aspire service discovery
could therefore direct API calls at the isolated worker listener. That listener
returned HTTP 500, causing the outbox to retry until workflow dispatch was
exhausted and the run was blocked.

## Decision

Configure the Functions resource's HTTP endpoint with target port `7119`
before referencing it from the API. This matches the `--port 7119` argument in
the Functions launch profile, so service discovery sends internal trigger calls
through the Functions host.

## Consequences

- API-to-workflow dispatch uses the Functions host route rather than the
  isolated worker listener.
- The Functions host port is an explicit AppHost setting and must stay aligned
  with the `--port` argument in the Functions launch profile.
- The initial discovery took longer because repeated outbox attempts surfaced
  only an HTTP 500; the Functions endpoint is now part of the AppHost wiring
  contract.
