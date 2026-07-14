# DevCommander

Personal autonomous-coding control plane. One human coordinates multi-repository missions through Telegram; DevCommander plans the work, runs one sandboxed coding squad per repository (Claude / Codex / Cursor `agent` / OpenCode), and applies a Coder → Critic → Verifier loop with durable SQLite recovery.

**Stack:** .NET 10 · NovaCore.Agents 3.1.7 · EF Core SQLite · Telegram.Bot · Linux Docker + bubblewrap

Architecture (processes, workflow, modules, coder prompt): [docs/architecture.md](docs/architecture.md).

## System requirements

| Environment | Requirement |
|---|---|
| Production host | Linux (Docker). Unprivileged user namespaces enabled for bubblewrap (e.g. `sysctl kernel.unprivileged_userns_clone=1` on compatible kernels). |
| Runtime | .NET 10 SDK/runtime; in Docker this is supplied by the image. |
| Tools on the host/image | `git`, `bubblewrap` (`bwrap`), coding CLIs: `claude`, `codex`, Cursor `agent`, `opencode` (failed install/auth/sandbox probe marks that runtime unavailable — no unsandboxed fallback). |
| Node | Required only where CLI installers need it (see `Dockerfile`). |
| Secrets | Env vars named by `DevCommander:Agents:*:ApiKeyEnvVar` (API keys never stored in config/DB/logs). |
| Telegram | Bot token + allowlisted chat IDs when `Telegram:Enabled=true`. |
| Disk | Persistent `{DataRoot}` volume for SQLite, missions, clones, worktrees, runtime session state. |
| Dev / tests | Cross-platform (Windows/macOS/Linux). Unit tests use fakes; real bubblewrap is Linux-only. |

Health check: `GET /health` on the configured ASP.NET URL (default container port `8080`).

## Running

### Local (development)

```bash
# 1. Configure DataRoot and agents in src/DevCommander/appsettings.json
#    (or override with env: DevCommander__DataRoot, etc.)

# 2. Set API key env vars (names from ApiKeyEnvVar)
export DEVCOMMANDER_COMMANDER_API_KEY=...
export DEVCOMMANDER_PLANNER_API_KEY=...
export DEVCOMMANDER_CRITIC_API_KEY=...

# 3. Optionally enable Telegram
#    Telegram__Enabled=true Telegram__BotToken=... Telegram__AllowedChatIds__0=12345

# 4. Put a mission file at {DataRoot}/missions/{slug}.md
# 5. Register repos via Telegram free-text / supervisor RegisterRepository capability

dotnet run --project src/DevCommander
```

On Windows/macOS, the Linux bubblewrap sandbox probe fails closed: coding runtimes stay **unavailable** until you run under Linux/Docker. Host orchestration, Telegram, DB, and planner/critic LLM calls still start.

### Docker Compose (recommended)

Secrets live in a **gitignored** `.env`; the compose file and `.env.example` are committed.

```bash
# Host: enable unprivileged user namespaces for bubblewrap first
# (e.g. sysctl kernel.unprivileged_userns_clone=1).

cp .env.example .env          # fill API keys + Telegram token / chat id
docker compose up --build -d
curl -fsS http://127.0.0.1:8080/health

# Mission files and SQLite persist on the host under ./data
mkdir -p data/missions
# edit data/missions/{slug}.md then /start {slug} in Telegram
```

| File | Committed? | Role |
|---|---|---|
| `docker-compose.yml` | yes | Build, port, `./data` volume, loads `.env` |
| `.env.example` | yes | Template of required env vars (no secrets) |
| `.env` | **no** (gitignored) | Your real API keys / bot token |

Compose injects `.env` into the container via `env_file`. Variable names must match `ApiKeyEnvVar` / ASP.NET `__` overrides (see `.env.example`).

### Docker (manual `docker run`)

```bash
docker build -t devcommander:latest .
docker run --rm -p 8080:8080 \
  -e DevCommander__DataRoot=/data \
  -e DEVCOMMANDER_COMMANDER_API_KEY \
  -e DEVCOMMANDER_PLANNER_API_KEY \
  -e DEVCOMMANDER_CRITIC_API_KEY \
  -e Telegram__Enabled=true \
  -e Telegram__BotToken \
  -e Telegram__AllowedChatIds__0=12345 \
  -v /var/lib/devcommander:/data \
  devcommander:latest

curl -fsS http://127.0.0.1:8080/health
```

Pin CLI versions in the image (see `Dockerfile`). Cursor `agent` may need to be mounted or installed by the operator.

Typical first-use flow over Telegram:

1. `/whoami` — confirm chat id is allowlisted
2. Free-text supervisor to `RegisterRepository`
3. Place `{DataRoot}/missions/{slug}.md`
4. `/start {slug}` → `/status {slug}` → `/approve {id}` when gated

## Configuration

`appsettings.json` (and environment overrides):

| Setting | Notes |
|---|---|
| `DevCommander:DataRoot` | Persistent root for DB, missions, clones, worktrees, runtime state |
| `DevCommander:DefaultBudgetUsd` | Default mission budget (default `5.0`) |
| `DevCommander:DefaultMissionWallTime` | Wall-time cap (default `08:00:00`) |
| `DevCommander:Agents:*` | OpenAI-compatible `BaseUrl` (**trailing slash required**), `ApiKeyEnvVar` (name only — never the secret), `Model`, `ProviderId`, token pricing |
| `DevCommander:Runtimes:*` | CLI executable names and estimated USD charges |
| `Telegram:Enabled` / `BotToken` / `AllowedChatIds` | Bot access allowlist |
| `Logging:LogLevel` | Default `Information`; ASP.NET `Warning` |

API keys are read from environment variables named by `ApiKeyEnvVar`. They are never written to configuration, SQLite, or logs.

## Logs (host process)

DevCommander uses the standard ASP.NET Core console logger. There is no separate log file by default.

| How | Command / note |
|---|---|
| Local run | Watch the terminal that started `dotnet run` |
| Docker | `docker logs -f <container>` |
| Verbosity | Raise categories via config/env, e.g. `Logging__LogLevel__Default=Debug` or `Logging__LogLevel__DevCommander=Debug` |
| What you will see | Startup (DataRoot, WAL mode), runtime/sandbox probe results, Telegram poll/inbox failures, notification delivery failures, git failures — structured `ILogger<T>` messages. Secrets and unbounded stdout/diffs are not logged. |

Notable events are also durable in SQLite (see debugging below); Telegram only gets completion / intervention notifications, not tool progress.

## Debugging agent and squad work

Telegram `/status {slug}` is a **database** status view (no agent call). For deeper inspection use `{DataRoot}/devcommander.db` (SQLite WAL):

```bash
sqlite3 {DataRoot}/devcommander.db
```

Useful tables:

| Table | Use |
|---|---|
| `Missions` | Spec snapshot/hash, status, budget, accounted cost, deadline |
| `Squads` | Per-repo worktree/branch, runtime, status, `LastPid`, `SessionId`, `LastCommittedSha` |
| `Tasks` | Phase, description, attempts, `BaselineCommit`, `Evidence`, `LastErrorSignature`, `PhaseSummary` |
| `SquadEvents` | Append-only squad action timeline (`Kind` + `Payload`) — primary debug trail for squad activity |
| `ApprovalRequests` | Gated-command state machine (`Pending` → `Approved` → `Executing` → `Consumed` / `Blocked`) |
| `Notifications` | Outbox (pending/sent/errors) |
| `TelegramUpdates` | Durable inbound updates |
| `agent_sessions` / `agent_messages` | NovaCore supervisor (`commander`) conversation persistence only |

Filesystem:

| Path | Use |
|---|---|
| `{DataRoot}/missions/{slug}.md` | Locked input mission |
| `{DataRoot}/worktrees/{missionId:N}/{repoId}/` | Live coder worktree — inspect diffs/commits |
| `{DataRoot}/runtime-state/{missionId:N}/{repoId}/home/` | Private per-squad runtime home / native CLI session state |
| `{DataRoot}/repos/{repoId}/` | Clone cache |

Coding CLI workers are external processes; their native transcripts live under runtime-state / the CLI’s own files, not in NovaCore tables. Recover coder context from `SessionId` + worktree + `Tasks` ledger (`Evidence`, baselines, phase summaries). Critic/planner one-shots are ephemeral (no conversation store).

Example queries:

```sql
SELECT Status, AttemptCount, LastErrorSignature, substr(Evidence,1,200) FROM Tasks;
SELECT At, Kind, substr(Payload,1,200) FROM SquadEvents ORDER BY At DESC LIMIT 50;
SELECT State, Operation, Attempt, CommandIndex FROM ApprovalRequests;
SELECT State, Severity, substr(Body,1,200), LastError FROM Notifications ORDER BY At DESC;
```

## Security model

- Telegram chats must be allowlisted (`AllowedChatIds`). Unknown chats are ignored except `/whoami`.
- Every coding worker is wrapped by `IWorkerSandbox` (Linux `bubblewrap`). There is **no unsandboxed fallback**. Failed probes mark the runtime unavailable.
- Workers may access only the assigned worktree, a private per-squad runtime home, and minimum worktree git metadata. Host secrets and git-push/deploy credentials are stripped from the worker environment.
- Only host services may push or run gated verifier commands. Gated commands require a single-use DB approval (`Pending → Approved → Executing → Consumed`). A crash in `Executing` becomes `Blocked` and never auto-replays.
- Push refspec is always `HEAD:refs/heads/mission/{repoId}/{missionSlug}`. Force pushes and pushes to `main`/`master` are rejected.

## Data layout

```
{DataRoot}/
  devcommander.db          # SQLite (WAL)
  missions/{slug}.md       # Locked human-authored mission files
  repos/{repoId}/          # Clone cache
  worktrees/{missionId:N}/{repoId}/
  runtime-state/{missionId:N}/{repoId}/home/
```

## Mission grammar

Mission file `{DataRoot}/missions/{missionSlug}.md` must contain seven non-empty sections:

1. **Repositories** — bullet list of registered repo IDs
2. **Goal**
3. **In-scope**
4. **Out-of-scope**
5. **Verification commands** — one `### {repoId}` subsection per repo (`repo default` or command bullets)
6. **Acceptance criteria**
7. **Runtime preference** — `default: {runtime}` plus optional `{repoId}: {runtime}` overrides

Runtime selection: repo-specific mission override → mission default → `Repo.DefaultRuntime`.

## Telegram commands

| Command | Behavior |
|---|---|
| `/missions` | List missions from SQLite |
| `/start {missionSlug}` | Validate, snapshot, plan, persist graph, start |
| `/status {missionSlug}` | DB status only (no agent) |
| `/approve {approvalId}` | Single-use gated-command approval |
| `/stop {missionSlug} {repoId}` | Kill process tree; durable Stopped |
| `/continue {missionSlug} {repoId} [guidance]` | Resume stopped/blocked work with attempts left |
| `/whoami` | Report chat id |
| free text | Supervisor agent (`commander`) |

Notifications are at-least-once via a durable outbox and are limited to completion, blocked/approval-needed, retries exhausted, budget/wall-time breach, and recovery summaries. Tool progress is never sent.

## Budget semantics

- Before every coder run, DevCommander **reserves** the configured estimated charge. If the reservation would exceed remaining mission budget, the run does not start.
- When the runtime reports authoritative cost, accounting **reconciles** to that value. Otherwise the estimate remains and is reported as **best-effort / estimated**.
- Wall-time cancellation applies to in-flight work when the mission deadline is reached.

## Recovery

On startup, reconciliation finishes interrupted durable transitions from DB + git state without reworking `Done` tasks, blocks recovered `Executing` approvals for human reconciliation, verifies process identity (PID + start time), and queues exactly one recovery summary within 60 seconds.

## Development

```bash
dotnet format DevCommander.sln --verify-no-changes
dotnet build DevCommander.sln
dotnet test DevCommander.sln
docker build -t devcommander:verify .
```

Cross-platform unit/integration tests use fakes for LLMs, Telegram, and coding CLIs. Real bubblewrap is exercised in the Linux image.
