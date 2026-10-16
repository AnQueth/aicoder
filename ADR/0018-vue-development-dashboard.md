# 0018: Vue development dashboard on the existing API origin

**Status:** Accepted

## Context

The headless platform already has product/repository configuration, developer
profiles, revisioned AI prompts, feature intake, and human workflow review gates.
Users need a browser client for those capabilities without copying API requests
or learning the console client's commands. The API deliberately only runs in
Development and uses a shared development key, not user authentication.

## Decision

Use Vue 3, TypeScript, and Vite for a responsive single-page workspace in
`Code/src/Collaborator.Web`. Build the site into the API's generated `wwwroot`
directory and serve it with ASP.NET Core default/static-file middleware.
Vite's local development server proxies `/api` to the API, keeping requests
same-origin without introducing a CORS policy or an additional backend service.
The .NET build does not invoke npm; build the frontend before building or
publishing the API.

VS Code provides an `AppHost + Vue` compound launch for frontend development.
It starts the existing Aspire launch alongside Vite on a strict port 5173 and
opens an Edge debugging session once Vite is listening. This reuses Vite's API
proxy and hot reload without adding a resource or dependency to the AppHost.
The API-hosted production build remains unchanged.
The compound first stops only this AppHost through Aspire to release Windows
DLL locks before its rebuild. Persistent data is retained; frontend-only launch
remains available when the existing backend must not be interrupted.
Compound stop propagation is disabled (`stopAll: false`) because Aspire launches
short-lived child debug sessions, including the database migrator. Their normal
completion must not terminate the AppHost and frontend. Users stop the two
long-running sessions independently or use VS Code's Stop All command.

The client asks for the existing development key and holds it only in memory.
It does not embed a key in the bundle, use a Vite secret variable, or persist
credentials or artifact data in browser storage. Disconnect clears the active
workspace. Public static assets contain no application state; all application
data remains behind the existing API-key middleware.

Add bounded, read-only product and run listing routes for discovery. Keep
configuration writes, immutable prompt revisions, submission idempotency,
revision-bound decisions, and workflow transitions on their existing routes.
Polling is visible, opt-out, abortable, and stops at terminal states. Failed
refreshes display an error and disable stale review actions. Generated content
is rendered as escaped text, never executable HTML.

## Consequences

- Users can configure context, submit features, inspect evidence/history, and
  record each human review in one website.
- Feature retries reuse an idempotency key for unchanged intent after a lost
  response; specialist defaults, explicit subsets, and empty selections keep
  their distinct backend meanings.
- Existing runs retain captured prompts and pinned sources when configuration
  changes. A browser refresh or disconnect requires entering the key again.
- There is no workflow designer, new identity system, or change to review
  policy. Anyone holding the shared development key has the API's existing
  capabilities; role labels do not provide authorization.
- Node.js is needed to build or develop the website, not to run the built API.
  Generated web assets are ignored by Git and included in normal API publish
  output after the frontend build.
- Frontend tests use mocked HTTP responses; directory endpoint HTTP tests use
  isolated in-memory data. Neither replaces a live Copilot workflow acceptance
  test or PostgreSQL integration verification.
