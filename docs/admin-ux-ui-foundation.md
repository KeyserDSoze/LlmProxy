# Admin UX second-pass audit and UI foundation

Updated 2026-10-11. Replaces the UI URL compatibility requirement; API compatibility and security policies are unchanged.

## Decisions

- **Canonical UI routes only**: `/admin/dashboard`, `/admin/catalog`, `/admin/models`, `/admin/system-one`, `/admin/infrastructure`, `/admin/routing`, `/admin/playground`, `/admin/credentials`, `/admin/users`, `/admin/governance`, `/admin/metrics`, `/admin/logs`, `/admin/audit`, `/admin/help`, `/admin/releases` and `/admin/me`. Old UI route aliases like `/admin/nodes`, `/admin/hardware`, `/admin/deployments` and `/admin/model-management` are retired. Server APIs remain unaffected. Canonical URLs retain tabs and selected node where relevant.
- **Mobile**: remove global 1100px minimum width. On small screens navigation is a real button-opened drawer with backdrop, close action, Escape support and route change closing. Page controls wrap and tables scroll within their panel rather than widening the entire document. Modals fit the viewport. A personal user portal with one route doesn't need a duplicated mobile navigation drawer.
- **Desktop**: keep a stable grouped sidebar and dense data tables, while using consistent spacing, typography, focus rings and shared header controls.
- **Themes**: semantic CSS tokens for app backgrounds, surfaces, borders, text, accent and semantic feedback. One persisted preference supports **System**, **Light** and **Dark**. System reacts to OS theme changes. Both the Admin shell, model management and audit panels, and personal portal reuse the same tokens. Never invert provider charts or status colors without verifying contrast.
- **Internationalization**: typed `useT()` + semantic message keys with English/Italian catalogs, stored in local preferences, initialized from browser language, set on `html[lang]`, updated without reload. Core global shell, dashboard actions and System One guided workflow are translated in this phase. **Other legacy operational pages still contain hard-coded English text:** this is tracked localization debt; avoid describing EN/IT as 100% complete until all visible strings, errors, help, empty states and aria labels are covered. Move remaining screens onto this single system incrementally, beginning with model catalog and agent pairing.
- **Accessibility**: focus-visible rings; modal opens at a focusable control, traps focus, handles Escape and restores prior focus. Tab groups respond to keyboard arrows/Home/End. Theme and language controls have explicit labels.

## Re-audited operator journeys

1. Dashboard → Install model → select hardware → model fit, runtime and parameters → install and monitor → start → enable routing → test actual inference. Current agent job completion may still rely on the Admin tab for final registration: this must be moved into a server-side durable job.
2. Dashboard → System One → connect an **already running** classifier to a node and actual service root → test typed response. Genuine Laya runtime installation remains tracked in [issue #19](https://github.com/KeyserDSoze/LlmProxy/issues/19); showing a connection form is **not** installing an engine.
3. Server pairing → Agent status → physical capacity → runtime health. Physical node vs runtime vs logical alias vs routing deployment must always remain distinct in the UI.
4. Usage governance vs physical admission must remain separate; metrics vs full-content request audit vs administrative audit must remain separate because permissions and retention differ.

## Remaining UX problems (prioritized, not hidden)

1. **System One end-to-end install**: Node Agent-managed Laya image/architecture support, true start/health/typed request tests, automatic model/runtime registration, retry-safe backend workflow, model readiness and recovery; tracked as issue #19.
2. **Localization completion**: migrate all operational pages from hard-coded English to the EN/IT dictionaries; automate untranslated-key coverage and test dynamic alerts, placeholders and help. The current release has a working language switch and translated navigation/dashboard/System One, **not a fully localized Admin**.
3. **Single model workspace**: the hardware inventory and the install catalog still share underlying component state while other model configuration lives in Models & Deployments. A future route-level model detail view should unify runtime, logical identity, provider limits, routing and diagnostics into a model-centric detail.
4. **Transactional setups**: improve partial-failure handling for creating logical models + deployments, and server-side durable install finalization. A UI wizard alone cannot guarantee atomicity.
5. **Server detail and troubleshooting**: replace generic error badges with concrete state cards for Agent offline / no runtime / running but unrouted / unhealthy; give next-step buttons based on actual permissions.
6. **High-density tables**: per-panel horizontal scrolling is the safe baseline on mobile; next iteration should offer a compact card mode for top operational tables, without removing tabular detail needed by desktop admins.
7. **Visual refinement**: audit contrast in both themes and mobile real devices, then normalize legacy page-specific spacing/tables/forms using shared primitives. Avoid scattering one-off colored callouts, buttons or design themes across pages.

## Tests / manual acceptance

- 375–390px mobile: no document-level horizontal scrolling in dashboard/models/infrastructure, menu opens and closes, refresh/back/forward keep the canonical route.
- Desktop: direct navigation, model catalog and server selection intact.
- Language Italian and English labels for shell, dashboard and System One; DOM lang reflects preference and survives reload.
- Light/dark/system: correct root theme and persisted preference; user portal inherits.
- Modals: keyboard close and restore focus; tab groups arrows/Home/End.
- Existing backend, PostgreSQL, Redis, Grafana and browser suites still pass.
