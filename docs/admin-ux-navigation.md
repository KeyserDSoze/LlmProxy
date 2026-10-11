# Admin experience: task-first information architecture

Date: 2026-10-11. Intent: make system operations discoverable without obscuring routing safety or changing endpoint contracts. This is a user-path audit, not a cosmetic redesign.

## Mental model: distinguish four objects

1. **Physical node / Agent** — a real machine, optionally connected through the outbound agent relay. Pair, recover and monitor it in **Infrastructure**.
2. **Installed runtime** — a concrete inference process/container with weights and resource usage, on a selected physical node. Install/start/stop/edit/uninstall it in **Install & manage models**. Agent-capable local runtimes include supported OpenAI-compatible engines; this is not the same as publishing a client-facing model.
3. **Logical model** — stable public alias with a protocol surface, used by applications, not a Docker image. Review aliases in **Models & Deployments**.
4. **Routing deployment** — maps a logical model to a node plus optional model-specific runtime root and capacity. The same node can run several models, and a model can have multiple eligible deployments.

**System One is a typed classifier API surface, not an installer.** LLMProxy currently forwards the existing classifier's unmodified typed JSON to `POST <runtime-root>/v1/systemone`, with client-facing `POST /v1/systemone`. Unlike the managed LLM runtimes, the Node Agent does not yet install Laya or another generic System One classifier from Hugging Face. Publishing an alias does not mean a classifier is running.

## New navigation

| Primary group | Page | Direct route | Purpose / primary task |
|---|---|---|---|
| Overview | Dashboard | `/admin/dashboard` | Actual fleet/traffic state, **What do you want to do?** shortcuts |
| Models & inference | Install & manage models | `/admin/catalog?tab=deploy&node=<node-id>` | Choose hardware, discover curated/Hub models, fit, install, start, edit, stop, uninstall; preserved node ID on refresh |
| Models & inference | Models & Deployments | `/admin/models?tab=models` | Client-facing aliases and deployment/routing eligibility, model/node views |
| Models & inference | System One classifiers | `/admin/system-one` | First-class classifier setup, explicit runtime location, existing classifiers/placements, test link |
| Models & inference | Routing | `/admin/routing` | Policy tuning and weights (no implicit model installation) |
| Models & inference | Playground | `/admin/playground?tab=chat`, `?tab=classifier` | Verify real forwarded Chat/typed classifier requests |
| Infrastructure | Infrastructure | `/admin/infrastructure?section=fleet` | Physical nodes, Agent pairing/recovery |
| Infrastructure | Hardware capacity | `/admin/infrastructure?section=capacity` | Physical metrics, limits and benchmarks |
| Infrastructure | Legacy inventory | `/admin/infrastructure?section=inventory&tab=inventory` | Existing deep links retained; links lead to dedicated model catalog |
| Access & policy | API Credentials | `/admin/credentials` | Shared API keys and their lifecycle |
| Access & policy | Users & Access | `/admin/users` | User provisioning, identity and membership |
| Access & policy | Usage & Governance | `/admin/governance` | Quotas, groups and rate limits, distinct from physical capacity |
| Monitor & audit | Request Metrics | `/admin/metrics` | Aggregate usage, latency, routing attempts and failures |
| Monitor & audit | Request Audit | `/admin/logs` | Inspect retained full request/response bodies with permissions |
| Monitor & audit | Administrative Audit | `/admin/audit` | Sensitive control/configuration events, not inference payloads |
| Platform | Help & Endpoints | `/admin/help` | Protocols, integration examples |
| Platform | Release Notes | `/admin/releases` | Version, updates, release provenance |

The old deeply nested Infrastructure → Inventory → Deploy models path remains working, but is no longer the only way to reach installation. No breaking database changes and no change to existing canonical API endpoints. Existing tab/node URL state and browser history remain supported. Contextual documentation is moved below the task-specific controls instead of occupying the first viewport.

## Operator journeys and definitions of success

### A: Add a server and install an LLM

1. Dashboard → **Add a server** → Infrastructure / Fleet. Pair/recover Agent and verify telemetry.
2. Dashboard → **Install an LLM** → **Install & manage models**, select hardware, review GPU/RAM/disk and model/runtime capabilities.
3. Choose a curated runtime or consciously opt into an unverified Hub model; edit context/output and concurrency before download. Model installation does not automatically mean healthy and routable.
4. Inspect installation progress. Start the runtime; test health and activate routing. Inspect the published alias/deployment and test a real completion in Playground.

### B: Connect a System One classifier

1. Ensure a compatible `POST /v1/systemone` classifier service is **already running** and reachable; current Node Agent does not install one.
2. Dashboard or sidebar → **System One classifiers**. Choose a registered node, create a logical alias (or reuse an existing classifier model), and specify the **real HTTP(S) runtime service root**, not the public LLMProxy URL. LLMProxy appends `/v1/systemone`.
3. Enter explicit concurrency, publish alias and bind deployment. A failure during the two-step existing Admin API workflow is recoverable by selecting the already-created logical model; never silently create another.
4. Validate through `/admin/playground?tab=classifier`; diagnose health/routing in Models & Deployments. Never blindly 'deploy to all active' when no classifier endpoint is known on each host.

### C: Diagnose "model not found" or "No Runtime"

1. Installed container state in **Install & manage models** is not the same as routing health.
2. **Models & Deployments** lists logical models and per-node enabled deployments; **Routing** lists policy/pressure and health.
3. **Playground** tests real inference, **Request Metrics** shows outcome, **Request Audit** shows retained exact calls, **Administrative Audit** shows controls changed.

### D: Manage access and policy

1. **Users & Access** sets identity and group membership.
2. **API Credentials** issues shared/per-user tokens according to permissions.
3. **Usage & Governance** sets rate and token budgets. Physical concurrency lives under Infrastructure.
4. **Request Metrics** and **Request Audit** verify usage and diagnose actual behavior.

## Safety / scope decisions

- Keep physical capacity, quotas, routing policy, logical models and installed runtimes as separate concepts with direct cross-links.
- Separate Chat/OpenAI installation from typed System One classifier connection. Don't label connection as installation or advertise a fake "Auto-install Laya" button.
- Remove blind `Deploy to all active` for System One until a concrete per-node runtime root is known. Manual classification deployment also requires an explicit endpoint.
- Never infer live classifier readiness merely from successful alias/deployment creation; instruct a real Playground test.
- Existing URLs, including selected hardware and tab history, remain functional; new `/admin/catalog` and `/admin/system-one` are first-class refresh-safe routes.
- Roles continue to gate write actions and private audit; an unauthorised user never receives a fake setup action.

## Further UX investments before visual redesign

1. **Atomic System One connection endpoint** with server-side validation and transactional create of logical alias + deployment, plus optional runtime authentication and diagnostic health probing. Current guided flow uses the existing pair of endpoints and explicitly supports partial failure.
2. **Real managed System One installer** only after defining supported classifier engines, model architectures, license, typed protocol adapter, hardware sizing, health readiness and end-to-end tests. Hugging Face files by themselves cannot act as a System One HTTP service.
3. **Model-detail routes** (`/admin/models/<id>`) and **hardware-detail routes** to keep search/filter/selection stable across F5. Current model list and hardware node ID deep-link cover the common workflow, not every modal or filter.
4. **Background finalisation and notifications** for model install/download/start independent of Admin tab lifetime; avoid relying on an open browser to finish deployment registration.
5. Guided contextual troubleshooting based on observed states (Agent offline, No Runtime, Started/Disabled, Unhealthy, insufficient resources), with precise action buttons and links.
6. Search across models/nodes/deployments and better small-screen table presentation. Visual polish is a separate phase after navigation and task usability is tested.

## Regression acceptance

- Dashboard actions reach the intended workspaces in one click.
- Catalog loads with Deploy models selected, hardware node is preserved in the URL and after hard refresh.
- System One direct links reload without reverting to Dashboard; setup clarifies the need for an existing classifier runtime and does not pretend to install it.
- Guided System One setup writes `surface=SystemOne`, no Chat-only capability flags, and an explicit runtime root to a specific existing node.
- No System One blind deploy-to-all and no manual classifier deployment with missing service root.
- Legacy Infrastructure tabs, published-model views, routing, credentials, audits and releases remain accessible.
