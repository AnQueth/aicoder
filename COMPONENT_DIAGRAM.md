# Collaborator Component Diagram

Current foundation architecture for the solution in
[IMPLEMENTATION_PLAN.md](./IMPLEMENTATION_PLAN.md). This is a .NET 10 REST +
Durable Functions + Copilot SDK workflow. Solid arrows represent requests or data
flows; dotted arrows represent configuration, coordination, or observability.

```mermaid
flowchart TB
    CLIENT["HTTP client / VS Code console / future UI"]
    PEOPLE["Product owner, BA, architect reviewer, developers"]
    CONFIG["Versioned configuration<br/>Products, repositories, roles, skill revisions"]

    subgraph PLATFORM["Collaborator - .NET 10 foundation"]
        API["ASP.NET Core REST API<br/>feature intake, product and skill config,<br/>status, artifact review, audit"]
        OUTBOX["Transactional outbox dispatcher<br/>reliable workflow starts and API events"]
        BRIDGE["Functions HTTP bridge<br/>internal workflow start and decision events"]

        subgraph WORKFLOW["Azure Durable Functions - isolated worker"]
            ORCH["Durable orchestration<br/>stages, waits, revision loops"]
            ACT["Activities<br/>generate specs, architecture, one bounded<br/>analysis per skill, cross-role synthesis, plans, code"]
        end

        subgraph AGENTS["GitHub Copilot SDK + repository exploration tools"]
            BA["Business Analyst agent<br/>drafts and revises specs using<br/>human answers to open questions"]
            ARCH["Architect agent<br/>analyzes approved spec against<br/>pinned repository evidence"]
            SPECIALIST["Developer skill agents<br/>one analysis for each captured,<br/>enabled developer-* skill"]
            COLLABORATOR["Cross-role collaborator<br/>source-linked agreements,<br/>conflicts, unresolved questions"]
            PLAN["Planner agent<br/>drafts and revises implementation plans"]
            REVIEWER["Reviewer selector<br/>assigns developer approval ownership"]
            CATALOG["Repository catalog service<br/>indexes repo snapshot for planning"]
            IMPLEMENTER["Code-change generator<br/>uses SDK tools in a feature worktree"]
        end

        SECRETS["Secret provider access<br/>Key Vault emulator / configuration values"]
        STORE["Run store + record persistence<br/>skill revisions and run snapshots,<br/>stage artifacts, plans, audit"]
    end

    subgraph DATA["Persistent resources"]
        PG[("PostgreSQL<br/>application state, runs, approvals, audit, outbox")]
        AZ[("Azurite<br/>local storage emulator")]
        KV[("Key Vault emulator<br/>Security--ApiKey / Security--WorkflowKey")]
        DURABLE[("Durable task backend<br/>history, timers, checkpoints")]
    end

    subgraph EXTERNAL["External dependencies"]
        REPO["Configured HTTPS, SSH, or local Git repository<br/>pinned base branch plus review-branch remote"]
        MODEL["GitHub Copilot CLI / runtime<br/>authenticated by local user or token"]
    end

    subgraph DEVHOST["Aspire AppHost"]
        APPHOST["Aspire AppHost<br/>starts Postgres, Azurite, Key Vault<br/>and the API + Functions host"]
        MIGRATIONS["EF migration runner"]
        DASH["Aspire dashboard<br/>health, logs, traces"]
    end

    CLIENT -->|"submit feature / answer questions / approve / revise"| API
    PEOPLE -->|"review BA spec, architecture, and plan"| API
    CONFIG -.->|"registered product + skill state"| API

    API -->|"application reads and writes"| STORE
    ACT -->|"application reads and writes"| STORE
    STORE -->|"EF Core / Npgsql"| PG
    PG -->|"pending outbox records"| OUTBOX
    OUTBOX -->|"internal HTTP start / decision delivery"| BRIDGE
    ORCH --> ACT
    BRIDGE --> ORCH
    ACT -->|"feature spec generation"| BA
    ACT -->|"architecture analysis after BA approval"| ARCH
    ACT -->|"one durable activity per skill; persist each result"| SPECIALIST
    SPECIALIST -->|"all original analyses"| COLLABORATOR
    ACT -->|"persist source-linked synthesis"| COLLABORATOR
    ACT -->|"all original analyses + synthesis + approved context"| PLAN
    ACT -->|"review ownership"| REVIEWER
    ACT -->|"catalog + snapshot"| CATALOG
    CATALOG -->|"read-only repository inspection"| REPO
    ACT -->|"approved spec + plan + pinned source"| IMPLEMENTER
    BA -->|"structured output"| MODEL
    ARCH -->|"structured output"| MODEL
    SPECIALIST -->|"one structured result per skill"| MODEL
    COLLABORATOR -->|"structured comparison"| MODEL
    PLAN -->|"structured output"| MODEL
    IMPLEMENTER -->|"SDK worktree edits + structured summary"| MODEL
    ACT -->|"prepare / publish review branch after required reviews"| REPO

    API -.->|"secret resolution"| SECRETS
    SECRETS --> KV
    ORCH -->|"durable checkpoints/timers"| DURABLE
    AZ -.->|"Azure Storage emulator"| DURABLE
    APPHOST -.->|"start and wire"| API
    APPHOST -.->|"start and wait for"| WORKFLOW
    APPHOST -.->|"provisions"| PG
    APPHOST -.->|"provisions"| AZ
    APPHOST -.->|"provisions"| KV
    APPHOST -.->|"complete before db services"| MIGRATIONS
    APPHOST -.-> DASH
    API -.->|"telemetry / health"| DASH
    ORCH -.->|"telemetry / health"| DASH
```

## Architecture boundaries

- The REST API is the system boundary for client requests and human approval responses.
  Development API-key-protected calls resume Durable waits; actor labels are not verified identities.
- Workflow ordering is BA specification approval, repository-grounded architecture
  analysis and human approval, then developer plan review. Architecture evidence is
  checked against the run's inspected snapshot; the artifact records input revisions,
  skill revision, and pinned commit.
- Enabled `developer-*` skills are snapshotted at submission. The workflow processes
  every captured skill in an independent bounded activity against the same
  approved context and repository snapshot. Results are persisted individually,
  checked for complete one-to-one coverage, then compared in a separate synthesis
  stage. Agreements, conflicts, and unresolved questions link to exact skill
  revisions. Planning receives both the complete original results and synthesis;
  human reviewers see them before approving the plan.
- The Aspire AppHost composes local development dependencies: PostgreSQL, Azurite,
  the Key Vault emulator, the API, and the Azure Functions host.
- PostgreSQL is the authoritative application database for runs, approvals, outbox records,
  and audit state. The Durable task backend tracks workflow execution progress and timers.
- Copilot agents receive on-demand read-only tools over the run-pinned repository.
  After plan approval, only the implementation agent receives all SDK built-ins
  and works directly in a temporary feature worktree at the pinned commit. The
  workflow collects and validates Git changes before publishing the review branch.
  The worktree is not an OS sandbox; shell tools can access resources available
  to the service identity.
- Repository URLs support HTTPS, SSH, and local `file://` sources with a configurable
  base branch. Remote access uses the host's existing Git/SSH credentials; managed
  credential provisioning, hosted pull requests, merge, and deployment are not
  implemented. The implementer can run builds/tests through SDK tools, but there
  is no independent build/test gate before publication.
- Outbox delivery retries up to ten times. The orchestration has no explicit
  activity retry policy; activity failures record a failed run. Selected AI
  artifact types get one same-session validation correction, which is not a
  Durable activity retry.
- The Key Vault emulator and user-secret seeding are development-only. Production
  identity and secret-management behavior have not been validated.
