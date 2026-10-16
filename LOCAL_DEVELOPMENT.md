# Local development: setup and running, in plain language

This guide is for running Collaborator on your own Windows computer. It does not
deploy anything to Azure. The Vue website is an optional sample UI; the API and
workflow services are the application.

## 1. What starts on your computer?

Think of the **AppHost** as the person who opens the shop. It starts the pieces
in the right order:

| Piece | Plain-language purpose |
|---|---|
| PostgreSQL | The filing cabinet: products, reviewers, decisions, and run records |
| Azurite | Local storage that remembers where long-running workflows stopped |
| Key Vault emulator | Supplies the local development keys to the services |
| Migrator | Updates the database layout, then exits; that exit is normal |
| Workflow service | Calls the AI and moves features through review and implementation |
| API | The front door used by clients |
| Aspire dashboard | Shows which pieces are healthy and their logs |
| Optional Vue sample | A browser client for trying the API |

You do not need a separately installed PostgreSQL server or an Azure subscription
for this local setup. Docker runs the local database and emulators.
Real AI calls do require authorized Copilot access and consume applicable usage.

## 2. Install these once

| Tool | Why |
|---|---|
| .NET SDK 10.0.401 | Builds and runs the backend; see [global.json](Code/global.json) |
| Docker Desktop or a compatible runtime | Runs the local database and emulators |
| Azure Functions Core Tools v4 with .NET 10 isolated support | Runs the workflow host |
| Git | Downloads repositories and publishes review branches |
| PowerShell 7 | Runs the helper scripts and commands in this guide |
| VS Code | Provides the F5 launch configurations |
| C#, C# Dev Kit, Azure Functions, and Aspire VS Code extensions | Support backend debugging and Aspire startup |
| Node.js 24 LTS and npm | Needed only for developing/building the Vue sample |
| Microsoft Edge | Used by the Vue F5 browser launch |
| GitHub Copilot CLI with authorized access | Supplies the login used by the AI runtime |

Open Docker and wait until it is running. After installing tools, open a new
PowerShell terminal so it picks up their locations.

Check the main commands:

```powershell
dotnet --version
docker info
func --version
git --version
pwsh --version
# Only needed for the sample UI:
node --version
npm --version
```

If a command is not found, fix that tool's installation before continuing.
See the [detailed prerequisites](Code/README.md#local-prerequisites) for context.

## 3. Open the right folder

In VS Code, open the **repository root**, the folder containing
[README.md](README.md) and [.vscode](.vscode).

On the current development computer, that folder is `H:\src\aicoder`.
Your clone can live somewhere else.

The F5 configurations will not be available in the same way if you open only
the backend or Vue subfolder.

In the VS Code PowerShell terminal, move to the backend working directory:

```powershell
# Starting at the repository root:
Set-Location .\Code
```

Unless a section says otherwise, the remaining terminal commands start here.

## 4. Prepare tools, certificates, and AI login

Restore the repository's pinned Aspire and EF command-line tools:

```powershell
dotnet tool restore
dotnet dev-certs https --trust
```

Accept the local certificate trust prompt if appropriate on your computer.
The Key Vault emulator may also ask you to trust its local certificate at startup.
Do not disable certificate verification to bypass a trust failure.

Start Copilot CLI and sign in using its login flow:

```powershell
copilot
```

Use the same Windows account that will run the AppHost. Copilot access, the
application API key, and Git credentials are three different things.
The SDK provides its pinned runtime during build; the CLI login supplies the
user's authentication. Follow your organization's rules for source-code access.

For private Git repositories, separately configure Git credentials or SSH for
this account. Prefer a credential manager; never put tokens into repository URLs,
tracked files, or feature prompts.

## 5. Create local keys once

Skip this step if this clone already runs on your computer and has working keys.
The application needs two different keys:

- `Parameters:api-key`: connects clients to the API.
- `Parameters:workflow-key`: protects internal workflow calls.

For a new setup only:

```powershell
$secrets = @{
    'Parameters:api-key' = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    'Parameters:workflow-key' = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
}
$secrets | ConvertTo-Json | dotnet user-secrets set --project src\Collaborator.AppHost
```

This stores the keys in your Windows user profile, not in the repository.
User secrets are local development storage, not an encrypted production vault.

**Do not rerun this just to reconnect the browser.** It replaces the keys.
To copy the existing API key without printing it:

```powershell
.\scripts\Copy-DevelopmentSecret.ps1
```

Paste it into the sample website's **Development API key** field.
Clear the clipboard/history when finished, especially if clipboard sync is enabled.
Never paste the key into chat, source files, screenshots, or URLs.

## 6. Easiest daily start: F5

### API and workflows, without the sample website

1. Make sure Docker is running.
2. Open **Run and Debug** in VS Code.
3. Select **Collaborator: Aspire extension (debug all services)**.
4. Press **F5**.
5. Open the Aspire dashboard using the link shown by the extension.
6. Wait for the API and workflows to become healthy.

Do not start a second AppHost while another copy is running. Stop the previous
debug session or CLI-owned AppHost first.

### API, workflows, and the Vue sample together

Before the first Vue launch, install its declared dependencies:

```powershell
npm --prefix src\Collaborator.Web ci
```

Then:

1. Stop existing debug sessions.
2. Select **Collaborator: AppHost + Vue** in **Run and Debug**.
3. Press **F5**.
4. Edge opens the sample at `http://127.0.0.1:5173/`.
5. Wait for the API and workflows to be healthy in Aspire.
6. Copy your key with the helper above, paste it, and select **Connect to API**.

The compound stops an existing instance of this AppHost before rebuilding.
That briefly interrupts the backend but does not delete saved data.
The browser may open before the backend is ready; wait, then connect.

Vue edits hot-reload. The sample forwards `/api` requests to the backend,
normally `http://localhost:5076`.

**Do not also run `npm run dev` in another terminal.** Only one process can own
port 5173. To develop Vue against an already-running backend, use
**Collaborator: Vue (Vite + Edge)** alone.

The launch configurations are in [launch.json](.vscode/launch.json).
The compound uses `stopAll: false` so the migrator's normal exit does not shut
down the other services. Use VS Code's **Stop All** debug command to finish.

## 7. Alternative: start without the VS Code debugger

From the [Code directory](Code):

```powershell
dotnet tool run aspire -- start --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
dotnet tool run aspire -- wait api --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
dotnet tool run aspire -- wait workflows --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

This uses the repository-pinned CLI and starts the AppHost in the background.
Follow its dashboard link. Treat a dashboard link containing a login token as
sensitive; do not share it.

For the optional Vue sample, open a second terminal in [Code](Code):

```powershell
npm --prefix src\Collaborator.Web run dev -- --port 5173 --strictPort
```

Keep that terminal running and open `http://127.0.0.1:5173/`. Press **Ctrl+C**
in that terminal to stop Vue. Do not start the F5 compound on top of this setup.

If you want the API itself to serve the compiled sample instead, stop backend
debug sessions/the AppHost, then:

```powershell
npm --prefix src\Collaborator.Web run build
dotnet tool run aspire -- start --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

After the API is healthy, open its URL, normally `http://localhost:5076/`.
Build Vue before backend build/publish; do not run the production Vue build at
the same time as a .NET build. The Vite development path does not need this
production build.

## 8. Try a feature safely

Use an isolated test repository, not an important working branch.

1. Register a product and its Git repository URL.
2. Configure enabled human reviewer profiles and the AI skills you need.
3. Submit a small feature.
4. Review the specification and architecture.
5. Submit developer proposals when using a proposal planning mode.
6. Review the resulting plan and its exact revision.
7. Approve implementation only when you intend to allow code generation.
8. Inspect the published review branch and independently run its tests.

The workflow downloads the repository and can execute commands on your computer.
A temporary checkout is **not a security sandbox**. Use only authorized,
trusted inputs and understand the service account's permissions.
Branch publication is not proof that tests passed and does not mean merge or deploy.

For a console client, leave the backend running and start
**Collaborator: API test client (interactive)** as a separate debug session.
Its existing sample repository argument is specific to the original development
computer; change it to your own test repository before use.
See [workflow examples](Code/WORKFLOW_EXAMPLE.md) for the full interaction.

## 9. Stop and restart without losing work

- VS Code launch: use **Stop All** for the debug sessions.
- Terminal Vue: press **Ctrl+C** in its own terminal.
- CLI AppHost: run the scoped stop command below.

```powershell
dotnet tool run aspire -- stop --apphost src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

Normal stop/start preserves the database and workflow state.
**Do not delete Docker volumes to fix a startup problem.** Do not use cleanup
commands that remove volumes unless you intentionally want to discard local data.

After stopping the backend, build if needed:

```powershell
dotnet build src\Collaborator.AppHost\Collaborator.AppHost.csproj
```

Then start again with either F5 or the CLI, not both.

## 10. Common problems

| What you see | What to do |
|---|---|
| DLL copy errors such as MSB3021/MSB3027 | Stop the old AppHost/debug sessions, allow child processes to exit, then rebuild. Do not build over running services. |
| `Port 5173 is already in use` | Stop the earlier Vue terminal/debug session. Do not kill every Node process; other applications may be using Node. |
| Migrator finishes and everything stops | Check that the compound still has `stopAll: false`; a successful migrator exit is expected. |
| Website opens but cannot connect | Wait for API health, verify the backend is running, then copy the existing API key again. |
| Browser asks for the key after refresh | Expected: the sample keeps the key in memory, not browser storage. |
| Database/emulator does not start | Check Docker and resource logs in the Aspire dashboard. Do not delete volumes as a shortcut. |
| AI authentication fails | Check Copilot login under the account running the worker; changing the application API key will not fix Copilot access. |
| Repository cannot be fetched/pushed | Check the configured URL, branch, network access, and Git permissions for the workflow account. |
| NU1900 vulnerability-metadata warning | Review package-feed access. A successful build with this warning is not a completed vulnerability check. |

The first startup downloads container images and can take longer than later runs.
AI generation can also take minutes; check resource logs rather than repeatedly
submitting the same feature.

## 11. Optional local checks

Stop the running backend before a normal backend build/test that writes to the
same output folders:

```powershell
dotnet build Collaborator.slnx
dotnet test tests\Collaborator.Tests\Collaborator.Tests.csproj --filter 'TestCategory!=LiveCopilot'
# Sample UI checks, if you installed its dependencies:
npm --prefix src\Collaborator.Web test
```

The test filter avoids tests explicitly categorized as live Copilot calls.
The repository has a documented source-search test failure; do not treat this
guide as a claim that the entire suite passes. See the
[test guide](Code/TEST_PROGRAM.md) for current evidence and limitations.

## More detail

- [Development and API reference](Code/README.md)
- [Local secret-copy helper](Code/scripts/Copy-DevelopmentSecret.ps1)
- [Production readiness checklist](PRODUCTION_READINESS_TODO.md)
- [Azure deployment direction](ADR/0021-azure-container-apps-deployment-target.md)
