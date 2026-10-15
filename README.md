# Collaborator

A development-only, headless .NET 10 platform that turns feature requests into
reviewable code changes using GitHub Copilot SDK agents and a human-reviewed
Durable Functions workflow.

## Current workflow

1. Register a product with its brief, Git repository, and optional base branch.
2. Submit a feature request through the REST API or console client.
3. Review and approve the business analyst's specification.
4. Review and approve the repository-grounded architecture assessment.
5. Run the user-selected specialist skills and compare their findings in a
   cross-role synthesis. Omit selection for enabled registry defaults, choose a
   subset, or choose none. Users define expertise and deliverables in skill prompts.
6. Review the implementation plan; all selected developers must approve it.
7. Implement in a temporary feature worktree and publish a validated change set
   to a unique review branch.

HTTPS, SSH, and local `file://` repositories are supported. The selected base
branch defaults to the repository's default branch, and each run pins its source
commit so later branch movement does not change its starting point. Private
repositories use Git/SSH credentials configured on the workflow host.

Publication means **ready for review**, not tested, accepted, or ready to release.
The implementer can run builds and tests through SDK tools, but no independent
post-implementation code-review, QA, UAT, or passing build/test gate is implemented.
The workflow does not create hosted pull requests, merge, or deploy.

## Getting started

Open this repository root in VS Code so its launch configurations are available.
Follow the [development setup guide](Code/README.md) for prerequisites, local
secrets, Copilot authentication, Aspire startup, and console-client usage.

To build and run local-only automated tests from the repository root:

```powershell
Set-Location .\Code
dotnet tool restore
dotnet build .\Collaborator.slnx
dotnet test .\tests\Collaborator.Tests\Collaborator.Tests.csproj --filter 'TestCategory!=LiveCopilot'
```

The `LiveCopilot` test calls a real model and consumes Copilot usage. Its
environment opt-in guard is currently commented out, so use the filter above
to exclude it. See the [test guide](Code/TEST_PROGRAM.md) before running live
tests or the API smoke script.

## Documentation

| Document | Purpose |
|---|---|
| [Development guide](Code/README.md) | Setup, configuration, REST contracts, workflow behavior, limits, and dated verification |
| [Workflow examples](Code/WORKFLOW_EXAMPLE.md) | Interactive feature reviews, scripted decisions, and locating generated code |
| [Test guide](Code/TEST_PROGRAM.md) | Local tests, live Copilot tests, and API smoke verification |
| [Component diagram](COMPONENT_DIAGRAM.md) | Current components, data flow, and execution boundaries |
| [Product specification](SPECIFICATION.md) | Target product requirements and the current implementation boundary |
| [Implementation plan](IMPLEMENTATION_PLAN.md) | Delivery direction, implemented checkpoint, and remaining work |
| [Architecture decisions](ADR/README.md) | Recorded decisions and their rationale |
| [Solution](Code/Collaborator.slnx) | Application and test projects |

The specification and implementation plan include future capabilities; the
development guide describes current operational behavior.

## Development boundaries

- The API, worker, client, and scripts use one current contract; compatibility
  aliases and retired workflow states are not maintained.
- Aspire starts PostgreSQL, Azurite, a Key Vault emulator, migrations, the REST
  API, and the Functions host for local development.
- Shared development API keys are not user authentication or role authorization.
  The API refuses non-Development startup.
- Repository source read by agents is sent to the Copilot runtime. Only use
  repositories you are authorized to process this way.
- The implementation worktree is not an OS sandbox. Its SDK tools can access
  resources available to the workflow service identity.
- Normal restarts preserve database and workflow state. Do not delete persistent
  volumes as a routine restart procedure.
