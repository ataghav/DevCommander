# Hyper-Care operator manual

How to run, connect, activate, and debug Hyper-Care Mode on a live DevCommander host.

**Spec:** [hyper-care-srs.md](hyper-care-srs.md) (Accepted) · **Sample config:** [hypercare-config.sample.json](hypercare-config.sample.json) · **Architecture:** [architecture.md](architecture.md) §7

While Hyper-Care is active, **normal missions are disabled** (`/start` is refused). Mode ends only with `/hc_off`.

---

## 1. Prerequisites checklist

| Need | Why |
|---|---|
| Telegram bot enabled + your chat allowlisted | All HC control is via Telegram |
| Repos registered in DevCommander | Each HC service maps to **exactly one** `repoId` |
| Coding runtime available in the container | Fix tracks need Claude/Codex/Cursor/OpenCode + sandbox |
| Host `gh` authenticated | Handover = push + `gh pr create` |
| Host can reach Grafana HTTP API | Watchers + `/hc_on` health check |
| Optional: host `az` logged in | Only if you configure `azureChecks` |
| LLM keys for **Triage** + **Investigate** | Plus existing Commander/Critic/coder keys |

Exclusive mode: stop or finish Normal missions before `/hc_on`. Activation **fails closed** if Starting/Running parent missions exist.

---

## 2. Connect Grafana

Hyper-Care does **not** use Grafana plugins or MCP. It calls the Grafana **HTTP API** with a Bearer token.

### 2.1 Create a Grafana service account token

1. In Grafana: **Administration → Service accounts** (or API keys, depending on version).
2. Create a service account with at least **Viewer** (or a role that can query your Loki/Prometheus datasources).
3. Add a token. Copy it once.

### 2.2 Put the token in the host environment

Never put the token in `config.json`. Only the **env var name** goes in config.

```bash
# .env (Docker Compose) or host env
HC_GRAFANA_TOKEN=glsa_...your_token...
```

Match the name to `grafana.tokenEnvVar` in config (sample uses `HC_GRAFANA_TOKEN`).

### 2.3 Point config at Grafana

Copy the sample and edit:

```bash
mkdir -p data/hypercare
cp docs/hypercare-config.sample.json data/hypercare/config.json
# edit data/hypercare/config.json
```

Set:

```json
"grafana": {
  "baseUrl": "https://YOUR-GRAFANA-HOST/",
  "tokenEnvVar": "HC_GRAFANA_TOKEN"
}
```

`/hc_on` calls `GET {baseUrl}/api/health` with that Bearer token. If this fails, activation lists the error and **does not** start Hyper-Care.

### 2.4 Write per-service queries

Each service needs one or more `grafanaQueries`. Each query is a raw HTTP call:

| Field | Meaning |
|---|---|
| `name` | Label for logs/events |
| `method` | Usually `POST` |
| `path` | Path under Grafana, e.g. `api/ds/query` |
| `bodyTemplate` | JSON body; `{fromMs}` / `{toMs}` are replaced with the poll window (unix millis) |

**How ingestion works:** the response JSON is walked; **every string leaf** becomes a candidate log line. Then redaction → include/exclude regexes → local signature grouping → triage LLM only for **new** signatures.

Example Loki-style query (adjust `datasource.uid` and LogQL to your stack):

```json
"grafanaQueries": [
  {
    "name": "loki-errors",
    "method": "POST",
    "path": "api/ds/query",
    "bodyTemplate": "{\"queries\":[{\"refId\":\"A\",\"datasource\":{\"uid\":\"loki\"},\"expr\":\"{app=\\\"checkout\\\"} |= `level=error`\",\"queryType\":\"range\"}],\"from\":\"{fromMs}\",\"to\":\"{toMs}\"}"
  }
]
```

**How to discover your real query:**

1. In Grafana Explore, run the query you want.
2. Open browser Network tab → find the `ds/query` (or equivalent) request.
3. Copy path + JSON body; replace absolute time range with `{fromMs}` / `{toMs}`.
4. Paste into `bodyTemplate` (escape quotes for JSON).

Metrics work the same way: any Grafana datasource query whose JSON response contains string leaves you can filter (or include numeric fields only if they appear as strings — prefer log/error queries for HC).

### 2.5 Tune filters before trusting triage

```json
"include": ["(?i)error|exception|timeout|5\\d\\d"],
"exclude": ["(?i)healthcheck|favicon"]
```

A line must match **an include** and **no exclude**. Noise never reaches the LLM (FR-HC-011). Start strict; loosen only when you miss real failures.

### 2.6 Optional Azure checks

If configured, host `az` must already be authenticated (service principal / managed identity / `az login` on the host image). Each check raises a candidate when exit code ≠ 0 or stdout does not match `expectRegex`. Leave `azureChecks` empty `[]` if unused.

### 2.7 Smoke-test Grafana outside DevCommander

```bash
# Health (same as /hc_on)
curl -fsS -H "Authorization: Bearer $HC_GRAFANA_TOKEN" \
  https://YOUR-GRAFANA-HOST/api/health

# One query (paste your body; set FROM/TO millis)
curl -fsS -H "Authorization: Bearer $HC_GRAFANA_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"queries":[...],"from":"FROM","to":"TO"}' \
  https://YOUR-GRAFANA-HOST/api/ds/query
```

If curl fails, `/hc_on` will fail the same way.

---

## 3. Wire secrets and agents (Docker)

Extend `.env` (see `.env.example`):

```bash
# Existing
DEVCOMMANDER_COMMANDER_API_KEY=...
DEVCOMMANDER_PLANNER_API_KEY=...
DEVCOMMANDER_CRITIC_API_KEY=...

# Hyper-Care LLM roles (names must match appsettings ApiKeyEnvVar)
DEVCOMMANDER_TRIAGE_API_KEY=...
DEVCOMMANDER_INVESTIGATE_API_KEY=...

# Grafana
HC_GRAFANA_TOKEN=...

# Telegram
Telegram__Enabled=true
Telegram__BotToken=...
Telegram__AllowedChatIds__0=...
```

Also ensure the container has working `gh auth` (and `az` if used). How you inject those is environment-specific (env token for `GH_TOKEN`, mounted credentials, etc.).

Rebuild:

```bash
docker compose up --build -d
curl -fsS http://127.0.0.1:8080/health
```

---

## 4. Register repos and place config

1. In Telegram: `/whoami` → confirm allowlisted chat id.
2. Register each `repoId` you will reference (supervisor free-text / `RegisterRepository`) with clone URL, default branch, default runtime, verify commands.
3. Place config:
   ```bash
   # Compose mounts ./data → /data
   ls data/hypercare/config.json
   ```
4. Confirm every `services[].repoId` exists in the DB and runtimes are available.

Edits to `config.json` after `/hc_on` do **not** apply to the current session (config is snapshotted + hashed at activation). Change config → `/hc_off` → `/hc_on`.

---

## 5. Activate Hyper-Care

### 5.1 Commands

| Command | When |
|---|---|
| `/hc_on` | Activate from `{DataRoot}/hypercare/config.json` |
| `/hc_status` | Session, budget, issues, queue, **DB-backed** source health |
| `/hc_off` | Stop watchers, freeze undecided issues, restore Normal mode |

Decision CTAs on issue cards:

| Action | Tap form | Typed form |
|---|---|---|
| Accept fix | `/go_{shortId}` | `/go {shortId} [severity]` |
| Suppress | `/nogo_{shortId}` | `/nogo {shortId}` |
| Severity | — | `/severity {shortId} {low\|medium\|high\|critical}` |
| Priority | — | `/priority {shortId} {n}` |
| Prefer / preempt same repo | `/hold_{shortId}` | `/hold {shortId}` |
| Requeue held | `/unhold_{shortId}` | `/unhold {shortId}` |

Fix-track controls (same as Normal, HC mission slug is `hc-{shortId}`):

- `/stop hc-{shortId} {repoId}`
- `/continue hc-{shortId} {repoId} [guidance]`
- `/approve {approvalId}` for gated verifier commands
- `/costs` — includes `triage` / `investigate` / coder roles

### 5.2 First activation

1. Clear Running/Starting Normal missions.
2. Send `/hc_on`.
3. **Success:** Telegram confirms session id, maxConcurrency, budget; bot menu switches to Hyper-Care commands; watchers start.
4. **Failure:** reply lists **every** validation problem (Grafana auth/health, missing token, bad repo mapping, unavailable runtime, `gh`/`az`, regex compile, exclusivity, production redaction, …). Mode stays Normal. Fix config/env and retry.

### 5.3 Steady state

1. Wait for issue cards (poll interval from config, default 60s).
2. `/go` or `/nogo` (optional severity on go).
3. Watch `/hc_status` for Queued / Running / Held / HandedOver.
4. On HandedOver you get a PR URL — **you** merge/deploy; DevCommander does not deploy.
5. `/hc_off` when the window ends. In-flight tracks may still finish; undecided issues stay frozen.

---

## 6. Debug and monitor what agents are doing

Hyper-Care was designed so the host is **not mute**. Use layers below — do not expect Telegram tool-progress spam.

### 6.1 Telegram (operator surface)

| Signal | Meaning |
|---|---|
| Issue card | New confirmed issue (AwaitingDecision) |
| Card edits | Occurrence/status updates (throttled ~60s) |
| Handover / Blocked / budget / recovery messages | Terminal or session events |
| `/hc_status` | Counts, running tracks, queue, source health from DB |
| `/costs` | Triage, investigate, critic, coder spend |
| `/status hc-{shortId}` | Underlying mission/squad status (Normal command still useful if you know the slug) |

### 6.2 Process logs (live)

```bash
docker logs -f devcommander-1
# or: docker compose logs -f devcommander
```

| Look for | Source |
|---|---|
| `HyperCare {Kind} session=…` | Durable HC events also mirrored to `ILogger` |
| `Hyper-Care session … attached; N watcher(s)` | Watchers started |
| `Agent cost recorded` / `Coder cost recorded` | LLM/CLI spend |
| Grafana/az/`gh` failures | Watcher or handover errors |

Raise DevCommander verbosity if needed:

```bash
Logging__LogLevel__DevCommander=Debug
```

### 6.3 SQLite — Hyper-Care event trail (primary debug)

```bash
# Compose: DB is on the host at ./data/devcommander.db
sqlite3 data/devcommander.db
```

```sql
-- Latest Hyper-Care activity (watchers, triage, queue, handover)
SELECT datetime(At) AS at, Kind, substr(Payload,1,200)
  FROM HyperCareEvents
 ORDER BY At DESC
 LIMIT 50;

-- Filter one session (use Id from HyperCareSessions)
SELECT datetime(At), Kind, IssueId, substr(Payload,1,240)
  FROM HyperCareEvents
 WHERE SessionId = '…'
 ORDER BY At DESC
 LIMIT 100;

-- Interesting kinds (non-exhaustive)
-- SessionStarted, SessionStopped, SessionRecovered, BudgetHalted
-- WatcherCycle, WatcherError, WatcherHealth
-- TriageRejected, TriageError, TriageSkippedBudget, SignatureMapped
-- IssueCreated, IssueQueued, IssueSuppressed, IssueClaimed, IssueHeld, …
-- FixTrackStarted, BranchPushed, IssueHandedOver, IssueBlocked, IssueFailed

SELECT ShortId, ServiceId, Status, OccurrenceCount, Severity, Priority,
       substr(Summary,1,80), PrUrl, LastError
  FROM HyperCareIssues
 ORDER BY LastSeenAt DESC;

SELECT ShortId, Status, AccountedCostUsd, BudgetUsd, MaxConcurrency, StartedAt
  FROM HyperCareSessions
 ORDER BY StartedAt DESC
 LIMIT 5;
```

### 6.4 Fix-track agents (investigate → coder → critic → verifier)

When you `/go`, DevCommander synthesizes a real **Mission** (`slug = hc-{shortId}`, branch `hypercare/{sessionShort}/{issueShort}`) and runs the normal SquadLoop.

| What | Where |
|---|---|
| Investigate / triage outcomes | `HyperCareEvents` + `AgentCostEntries` (`triage`, `investigate`) |
| Coder / critic / verify timeline | `SquadEvents` for that mission’s squad |
| Task attempts / evidence | `Tasks` |
| Live diff | `{DataRoot}/worktrees/{missionId}/{repoId}/` |
| CLI native session | `{DataRoot}/runtime-state/{missionId}/{repoId}/home/` |
| Mission status | `/status hc-{shortId}` or `Missions` / `Squads` tables |

```sql
-- Map issue → mission
SELECT ShortId, MissionId, Status, Branch, PrUrl FROM HyperCareIssues WHERE ShortId = 'ab12cd34';

-- Squad timeline for that mission
SELECT datetime(At), Kind, substr(Payload,1,200)
  FROM SquadEvents
 WHERE SquadId IN (SELECT Id FROM Squads WHERE MissionId = '…')
 ORDER BY At DESC
 LIMIT 50;

SELECT AgentRole, COUNT(*), SUM(TotalCostUsd)
  FROM AgentCostEntries
 GROUP BY AgentRole;
```

Triage and investigate are **one-shot** NovaCore calls (no chat transcript table). Persistence is the structured result + cost row + `HyperCareEvents`.

### 6.5 Source health

`/hc_status` reads **durable** last-known health (not only in-memory). After a restart you should still see the last success/error recorded for each service until the next poll updates it.

---

## 7. Common failures

| Symptom | Likely cause | What to do |
|---|---|---|
| `/hc_on` lists Grafana unreachable / 401 | Bad URL, token, or network from container | Fix `baseUrl` / `HC_GRAFANA_TOKEN`; curl from inside the container |
| `/hc_on` lists repo / runtime problems | Unregistered `repoId` or sandbox/runtime unavailable | Register repo; run Linux/Docker with working CLIs |
| `/hc_on` refuses parent missions | Normal mission Starting/Running | Finish or leave Normal mode first |
| No issue cards | Filters too strict, empty Grafana window, or triage rejecting | Check `WatcherCycle` / `TriageRejected`; loosen include or fix query |
| Cards but no triage spend | All signatures cached / BudgetHalted | `/hc_status` + `TriageSkippedBudget` events |
| Running forever / Blocked | Critic/verifier fail or `gh` fail after push | `SquadEvents`, `LastError`, branch name on Blocked card |

---

## 8. Quick start (copy/paste)

```bash
# 1. Secrets
cp .env.example .env   # fill Telegram, LLM keys, HC_GRAFANA_TOKEN, triage/investigate keys

# 2. Config
mkdir -p data/hypercare
cp docs/hypercare-config.sample.json data/hypercare/config.json
# edit baseUrl, repoIds, LogQL/queries, filters

# 3. Run
docker compose up --build -d
curl -fsS http://127.0.0.1:8080/health

# 4. Telegram
# /whoami → register repos → /hc_on → /hc_status → /go … → /hc_off

# 5. Debug
docker compose logs -f devcommander
sqlite3 data/devcommander.db "SELECT datetime(At), Kind, substr(Payload,1,120) FROM HyperCareEvents ORDER BY At DESC LIMIT 30;"
```

---

## Revision

| Date | Change |
|---|---|
| 2026-07-24 | Initial operator manual (Grafana, activate, debug) |
