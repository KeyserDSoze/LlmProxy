import { useSyncExternalStore } from 'react'

export type UiLocale = 'en' | 'it'
export type UiTheme = 'system' | 'light' | 'dark'
export type UiPreferences = { locale: UiLocale; theme: UiTheme }

const PREFERENCES_KEY = 'llmproxy.admin.preferences.v1'
function safelyRead(): Partial<UiPreferences> {
  try {
    const raw = window.localStorage.getItem(PREFERENCES_KEY)
    return raw ? JSON.parse(raw) as Partial<UiPreferences> : {}
  } catch { return {} }
}
function initialPreferences(): UiPreferences {
  const saved = safelyRead()
  return {
    locale: saved.locale === 'it' || saved.locale === 'en'
      ? saved.locale : (navigator.language.toLowerCase().startsWith('it') ? 'it' : 'en'),
    theme: saved.theme === 'light' || saved.theme === 'dark' || saved.theme === 'system'
      ? saved.theme : 'system'
  }
}
let snapshot: UiPreferences = typeof window === 'undefined'
  ? { locale: 'en', theme: 'system' } : initialPreferences()
const listeners = new Set<() => void>()

export function effectiveTheme(theme: UiTheme): 'dark' | 'light' {
  if (theme === 'system') {
    return typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches
      ? 'dark' : 'light'
  }
  return theme
}
function applyPreferences() {
  if (typeof document === 'undefined') return
  document.documentElement.dataset.theme = effectiveTheme(snapshot.theme)
  document.documentElement.lang = snapshot.locale
  document.documentElement.style.colorScheme = effectiveTheme(snapshot.theme)
}
function notify() {
  applyPreferences()
  listeners.forEach(fn => fn())
}
export function setUiPreferences(value: Partial<UiPreferences>) {
  const next = { ...snapshot, ...value }
  if (next.locale !== 'en' && next.locale !== 'it') return
  if (!['light', 'dark', 'system'].includes(next.theme)) return
  if (snapshot.locale === next.locale && snapshot.theme === next.theme) return
  snapshot = next
  try { window.localStorage.setItem(PREFERENCES_KEY, JSON.stringify(next)) } catch { /* private browsing */ }
  notify()
}
export function getUiPreferences(): UiPreferences { return snapshot }
export function useUiPreferences() {
  const value = useSyncExternalStore(subscribe, getUiPreferences, () => ({ locale: 'en', theme: 'system' } as UiPreferences))
  return { ...value, setLocale: (locale: UiLocale) => setUiPreferences({ locale }),
    setTheme: (theme: UiTheme) => setUiPreferences({ theme }) }
}
function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
if (typeof window !== 'undefined') {
  applyPreferences()
  window.matchMedia?.('(prefers-color-scheme: dark)').addEventListener?.('change', () => {
    if (snapshot.theme === 'system') notify()
  })
  window.addEventListener('storage', event => {
    if (event.key !== PREFERENCES_KEY) return
    const saved = safelyRead()
    if ((saved.locale === 'it' || saved.locale === 'en') &&
        (saved.theme === 'light' || saved.theme === 'dark' || saved.theme === 'system')) {
      snapshot = { locale: saved.locale, theme: saved.theme }
      notify()
    }
  })
}

// Every UI string that we migrate uses a stable semantic key rather than
// looking up the rendered English text. New strings must be added in BOTH catalogs.
const en = {
  'nav.dashboard': 'Dashboard',
  'nav.modelsGroup': 'Models & inference',
  'nav.catalog': 'Install & manage models',
  'nav.models': 'Models & deployments',
  'nav.systemOne': 'System One classifiers',
  'nav.routing': 'Routing',
  'nav.playground': 'Playground',
  'nav.infrastructureGroup': 'Infrastructure',
  'nav.infrastructure': 'Servers & hardware',
  'nav.accessGroup': 'Access & policy',
  'nav.credentials': 'API credentials',
  'nav.users': 'Users & access',
  'nav.governance': 'Usage & governance',
  'nav.monitorGroup': 'Monitor & audit',
  'nav.metrics': 'Request metrics',
  'nav.logs': 'Request audit',
  'nav.audit': 'Administrative audit',
  'nav.platformGroup': 'Platform',
  'nav.help': 'Help & endpoints',
  'nav.releases': 'Release notes',
  'nav.open': 'Open navigation',
  'nav.close': 'Close navigation',
  'nav.overview': 'Overview',
  'nav.gateway': 'AI gateway',
  'nav.footer': 'OpenAI-compatible gateway',
  'common.refresh': 'Refresh',
  'common.loading': 'Loading gateway state…',
  'common.cancel': 'Cancel',
  'common.save': 'Save',
  'common.configure': 'Configure',
  'common.next': 'Continue',
  'common.theme': 'Theme',
  'common.language': 'Language',
  'theme.system': 'System',
  'theme.light': 'Light',
  'theme.dark': 'Dark',
  'locale.en': 'English',
  'locale.it': 'Italiano',
  'page.dashboard.title': 'Gateway dashboard',
  'page.dashboard.desc': 'Fleet status, traffic and shortcuts to common tasks.',
  'page.catalog.title': 'Install & manage models',
  'page.catalog.desc': 'Discover, install and operate inference runtimes on your servers.',
  'page.models.title': 'Models & deployments',
  'page.models.desc': 'Client-facing model names, placements and routing state.',
  'page.systemOne.title': 'System One classifiers',
  'page.systemOne.desc': 'Connect, validate and route existing typed classifier services.',
  'page.infrastructure.title': 'Servers & hardware',
  'page.infrastructure.desc': 'Manage Linux Agents, fleet access, capacity and inventory.',
  'page.routing.title': 'Routing policy',
  'page.routing.desc': 'Control how eligible deployments receive requests.',
  'page.credentials.title': 'API credentials',
  'page.credentials.desc': 'Create, inspect and rotate gateway credentials.',
  'page.users.title': 'Users & access',
  'page.users.desc': 'Manage identities and account access.',
  'page.governance.title': 'Usage & governance',
  'page.governance.desc': 'Configure group quotas and request budgets.',
  'page.metrics.title': 'Inference observability',
  'page.metrics.desc': 'Requests, success rate, usage and latency.',
  'page.playground.title': 'Model & classifier playground',
  'page.playground.desc': 'Verify inference over your gateway routes.',
  'page.logs.title': 'Request & response audit',
  'page.logs.desc': 'Inspect retained inference request payloads.',
  'page.audit.title': 'Administrative audit trail',
  'page.audit.desc': 'Configuration and privileged activity history.',
  'page.help.title': 'Endpoint & platform guide',
  'page.help.desc': 'API contracts and integration instructions.',
  'page.releases.title': 'Version & release notes',
  'page.releases.desc': 'Versions, updates and release history.',
  'dashboard.tasks.title': 'What would you like to do?',
  'dashboard.tasks.desc': 'Start with your goal, not a configuration submenu.',
  'dashboard.tasks.install.title': 'Install a model',
  'dashboard.tasks.install.desc': 'Select a server, compare requirements and run an LLM.',
  'dashboard.tasks.install.action': 'Browse models',
  'dashboard.tasks.classifier.title': 'Connect a classifier',
  'dashboard.tasks.classifier.desc': 'Register an existing System One service and make it routable.',
  'dashboard.tasks.classifier.action': 'Configure System One',
  'dashboard.tasks.server.title': 'Add a server',
  'dashboard.tasks.server.desc': 'Pair a Linux Agent and inspect hardware.',
  'dashboard.tasks.server.action': 'Manage servers',
  'dashboard.tasks.publish.title': 'Publish a model',
  'dashboard.tasks.publish.desc': 'Review client-facing aliases and active deployments.',
  'dashboard.tasks.publish.action': 'View model routes',
  'dashboard.tasks.test.title': 'Test a request',
  'dashboard.tasks.test.desc': 'Verify Chat or classifier traffic through the gateway.',
  'dashboard.tasks.test.action': 'Try inference',
  'dashboard.summary.nodes': 'healthy servers',
  'dashboard.summary.deployments': 'enabled deployments',
  'dashboard.summary.classifiers': 'System One models',
  'dashboard.kpi.fleet': 'Fleet health',
  'dashboard.kpi.models': 'Logical models',
  'dashboard.kpi.deployments': 'Deployments',
  'dashboard.kpi.active': 'Active requests',
  'dashboard.kpi.today': 'Requests today',
  'dashboard.kpi.success': 'Success rate',
  'dashboard.kpi.output': 'Output tokens',
  'dashboard.fleet.title': 'Inference fleet',
  'dashboard.fleet.node': 'Node',
  'dashboard.fleet.status': 'Status',
  'dashboard.fleet.root': 'Service root',
  'dashboard.fleet.latency': 'Latency',
  'dashboard.fleet.streak': 'Health streak',
  'dashboard.fleet.last': 'Last check',
  'systemOne.title': 'System One classifiers',
  'systemOne.intro': 'Connect an existing classifier service. Requests use the typed POST /v1/systemone API and share your normal routing policies.',
  'systemOne.what.title': 'How it works',
  'systemOne.what.desc': 'Three steps to connect an existing classifier.',
  'systemOne.step1.title': 'Prepare the runtime',
  'systemOne.step1.desc': 'The Node Agent does not yet install Laya automatically. Start a compatible service with POST /v1/systemone first.',
  'systemOne.step2.title': 'Connect the server',
  'systemOne.step2.desc': 'Choose the server hosting the classifier and its HTTP(S) service root.',
  'systemOne.step3.title': 'Publish and verify',
  'systemOne.step3.desc': 'Register its logical name, route requests and test a real classification.',
  'systemOne.setup.title': 'Connect a System One runtime',
  'systemOne.setup.desc': 'An existing classifier service is required. This step does not download model weights.',
  'systemOne.mode': 'Model registration',
  'systemOne.mode.new': 'Publish a new System One model',
  'systemOne.mode.existing': 'Connect an existing classifier model',
  'systemOne.logical': 'Public model name',
  'systemOne.provider': 'Provider model identity',
  'systemOne.existing': 'Existing System One model',
  'systemOne.hardware': 'Target server',
  'systemOne.root': 'Classifier service root (URL)',
  'systemOne.limit': 'Maximum concurrent requests',
  'systemOne.submit': 'Connect and publish classifier',
  'systemOne.submit.busy': 'Connecting…',
  'systemOne.registered': 'Registered System One models',
  'systemOne.empty': 'No classifier has been registered yet.',
  'systemOne.test': 'Open classifier playground',
  'systemOne.modelRoutes': 'Manage model routes',
  'systemOne.step.identity': 'Step 1 — Identify classifier',
  'systemOne.step.location': 'Step 2 — Select server and endpoint',
  'systemOne.step.publish': 'Step 3 — Publish and test',
  'systemOne.rootHelp': 'Enter the service root only. LLMProxy appends /v1/systemone. Publishing does not install software.',
  'systemOne.unavailable': 'No enabled servers. Add or enable a server first.',
  'systemOne.select': 'Select a classifier',
  'systemOne.selectServer': 'Select an enabled server',
  'systemOne.localRoute': 'Runtime route',
  'systemOne.routeEnabled': 'Routes enabled',
  'systemOne.endpoint': 'Provider identity',
  'systemOne.done': 'A deployment is registered. Test a real classifier response before production traffic.',
  'help.documentation': 'Page documentation'
} as const

const it: Record<keyof typeof en, string> = {
  'nav.dashboard': 'Panoramica',
  'nav.modelsGroup': 'Modelli e inferenza',
  'nav.catalog': 'Installa e gestisci modelli',
  'nav.models': 'Modelli e deployment',
  'nav.systemOne': 'Classificatori System One',
  'nav.routing': 'Instradamento',
  'nav.playground': 'Area di test',
  'nav.infrastructureGroup': 'Infrastruttura',
  'nav.infrastructure': 'Server e hardware',
  'nav.accessGroup': 'Accessi e policy',
  'nav.credentials': 'Credenziali API',
  'nav.users': 'Utenti e accessi',
  'nav.governance': 'Utilizzo e governance',
  'nav.monitorGroup': 'Monitoraggio e audit',
  'nav.metrics': 'Metriche richieste',
  'nav.logs': 'Audit richieste',
  'nav.audit': 'Audit amministrativo',
  'nav.platformGroup': 'Piattaforma',
  'nav.help': 'Guida ed endpoint',
  'nav.releases': 'Note di rilascio',
  'nav.open': 'Apri navigazione',
  'nav.close': 'Chiudi navigazione',
  'nav.overview': 'Panoramica',
  'nav.gateway': 'Gateway AI',
  'nav.footer': 'Gateway compatibile con OpenAI',
  'common.refresh': 'Aggiorna',
  'common.loading': 'Caricamento dati del gateway…',
  'common.cancel': 'Annulla',
  'common.save': 'Salva',
  'common.configure': 'Configura',
  'common.next': 'Continua',
  'common.theme': 'Tema',
  'common.language': 'Lingua',
  'theme.system': 'Sistema',
  'theme.light': 'Chiaro',
  'theme.dark': 'Scuro',
  'locale.en': 'English',
  'locale.it': 'Italiano',
  'page.dashboard.title': 'Panoramica gateway',
  'page.dashboard.desc': 'Stato dei server, traffico e accesso rapido alle operazioni.',
  'page.catalog.title': 'Installa e gestisci modelli',
  'page.catalog.desc': 'Scopri, installa e avvia modelli di inferenza sui tuoi server.',
  'page.models.title': 'Modelli e deployment',
  'page.models.desc': 'Nomi pubblici, distribuzione e instradamento dei modelli.',
  'page.systemOne.title': 'Classificatori System One',
  'page.systemOne.desc': 'Collega, verifica e instrada classificatori già in esecuzione.',
  'page.infrastructure.title': 'Server e hardware',
  'page.infrastructure.desc': 'Gestisci Agent Linux, accesso ai server, capacità e inventario.',
  'page.routing.title': 'Policy di instradamento',
  'page.routing.desc': 'Decidi come distribuire le richieste tra i deployment disponibili.',
  'page.credentials.title': 'Credenziali API',
  'page.credentials.desc': 'Crea, controlla e ruota le credenziali del gateway.',
  'page.users.title': 'Utenti e accessi',
  'page.users.desc': 'Gestisci identità e permessi di accesso.',
  'page.governance.title': 'Utilizzo e governance',
  'page.governance.desc': 'Configura quote, gruppi e limiti di richiesta.',
  'page.metrics.title': 'Osservabilità inferenza',
  'page.metrics.desc': 'Richieste, esiti, utilizzo e latenza.',
  'page.playground.title': 'Test modelli e classificatori',
  'page.playground.desc': 'Verifica le richieste sui percorsi reali del gateway.',
  'page.logs.title': 'Audit richieste e risposte',
  'page.logs.desc': 'Analizza i payload delle richieste conservate.',
  'page.audit.title': 'Registro attività amministrative',
  'page.audit.desc': 'Cronologia configurazioni e operazioni privilegiate.',
  'page.help.title': 'Guida alla piattaforma',
  'page.help.desc': 'Contratti API e istruzioni di integrazione.',
  'page.releases.title': 'Versioni e note di rilascio',
  'page.releases.desc': 'Versioni, aggiornamenti e cronologia rilasci.',
  'dashboard.tasks.title': 'Cosa vuoi fare?',
  'dashboard.tasks.desc': 'Parti dall’obiettivo, non da un sottomenu tecnico.',
  'dashboard.tasks.install.title': 'Installa un modello',
  'dashboard.tasks.install.desc': 'Scegli un server, verifica i requisiti e avvia un LLM.',
  'dashboard.tasks.install.action': 'Esplora modelli',
  'dashboard.tasks.classifier.title': 'Collega un classificatore',
  'dashboard.tasks.classifier.desc': 'Registra un servizio System One esistente e rendilo raggiungibile.',
  'dashboard.tasks.classifier.action': 'Configura System One',
  'dashboard.tasks.server.title': 'Aggiungi un server',
  'dashboard.tasks.server.desc': 'Associa un Agent Linux e consulta l’hardware.',
  'dashboard.tasks.server.action': 'Gestisci server',
  'dashboard.tasks.publish.title': 'Pubblica un modello',
  'dashboard.tasks.publish.desc': 'Controlla alias pubblici e deployment attivi.',
  'dashboard.tasks.publish.action': 'Vedi instradamento',
  'dashboard.tasks.test.title': 'Prova una richiesta',
  'dashboard.tasks.test.desc': 'Verifica richieste Chat o classificatori attraverso il gateway.',
  'dashboard.tasks.test.action': 'Prova inferenza',
  'dashboard.summary.nodes': 'server sani',
  'dashboard.summary.deployments': 'deployment attivi',
  'dashboard.summary.classifiers': 'modelli System One',
  'dashboard.kpi.fleet': 'Stato server',
  'dashboard.kpi.models': 'Modelli logici',
  'dashboard.kpi.deployments': 'Deployment',
  'dashboard.kpi.active': 'Richieste attive',
  'dashboard.kpi.today': 'Richieste oggi',
  'dashboard.kpi.success': 'Tasso di successo',
  'dashboard.kpi.output': 'Token generati',
  'dashboard.fleet.title': 'Server di inferenza',
  'dashboard.fleet.node': 'Server',
  'dashboard.fleet.status': 'Stato',
  'dashboard.fleet.root': 'Endpoint servizio',
  'dashboard.fleet.latency': 'Latenza',
  'dashboard.fleet.streak': 'Esiti recenti',
  'dashboard.fleet.last': 'Ultimo controllo',
  'systemOne.title': 'Classificatori System One',
  'systemOne.intro': 'Collega un servizio di classificazione esistente. Le richieste usano POST /v1/systemone e condividono le normali policy di routing.',
  'systemOne.what.title': 'Come funziona',
  'systemOne.what.desc': 'Tre passaggi per collegare un classificatore esistente.',
  'systemOne.step1.title': 'Prepara il runtime',
  'systemOne.step1.desc': 'Il Node Agent non installa ancora Laya automaticamente. Avvia prima un servizio compatibile con POST /v1/systemone.',
  'systemOne.step2.title': 'Collega il server',
  'systemOne.step2.desc': 'Scegli il server del classificatore e l’indirizzo HTTP(S) del servizio.',
  'systemOne.step3.title': 'Pubblica e verifica',
  'systemOne.step3.desc': 'Registra il nome pubblico, instrada le richieste e prova una classificazione reale.',
  'systemOne.setup.title': 'Collega un runtime System One',
  'systemOne.setup.desc': 'È necessario un servizio di classificazione già attivo. Non vengono scaricati pesi del modello.',
  'systemOne.mode': 'Registrazione del modello',
  'systemOne.mode.new': 'Pubblica un nuovo modello System One',
  'systemOne.mode.existing': 'Collega un modello di classificazione esistente',
  'systemOne.logical': 'Nome pubblico del modello',
  'systemOne.provider': 'Identificativo modello del provider',
  'systemOne.existing': 'Modello System One esistente',
  'systemOne.hardware': 'Server di destinazione',
  'systemOne.root': 'Indirizzo del servizio di classificazione',
  'systemOne.limit': 'Numero massimo di richieste simultanee',
  'systemOne.submit': 'Collega e pubblica classificatore',
  'systemOne.submit.busy': 'Collegamento in corso…',
  'systemOne.registered': 'Modelli System One registrati',
  'systemOne.empty': 'Nessun classificatore registrato.',
  'systemOne.test': 'Apri test classificatori',
  'systemOne.modelRoutes': 'Gestisci instradamento modelli',
  'systemOne.step.identity': 'Passaggio 1 — Identifica il classificatore',
  'systemOne.step.location': 'Passaggio 2 — Seleziona server ed endpoint',
  'systemOne.step.publish': 'Passaggio 3 — Pubblica e verifica',
  'systemOne.rootHelp': 'Inserisci solo la radice del servizio: LLMProxy aggiunge /v1/systemone. La pubblicazione non installa software.',
  'systemOne.unavailable': 'Nessun server abilitato. Aggiungi o abilita prima un server.',
  'systemOne.select': 'Seleziona classificatore',
  'systemOne.selectServer': 'Seleziona un server abilitato',
  'systemOne.localRoute': 'Percorso runtime',
  'systemOne.routeEnabled': 'Instradamenti abilitati',
  'systemOne.endpoint': 'Identificativo provider',
  'systemOne.done': 'Deployment registrato. Verifica una risposta reale prima dell’uso in produzione.',
  'help.documentation': 'Documentazione pagina'
}

export type TranslationKey = keyof typeof en
export function translate(locale: UiLocale, key: TranslationKey): string {
  return locale === 'it' ? it[key] : en[key]
}
export function useT() {
  const { locale } = useUiPreferences()
  return (key: TranslationKey) => translate(locale, key)
}
