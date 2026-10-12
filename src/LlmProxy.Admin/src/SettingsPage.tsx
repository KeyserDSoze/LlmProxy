import AppearanceControls from './AppearanceControls'
import { useT } from './uiPreferences'

export default function SettingsPage({ productVersion, onOpenReleases }: {
  productVersion: string | null
  onOpenReleases: () => void
}) {
  const t = useT()
  return <div className="settingsLayout">
    <section className="panel settingsPanel">
      <div className="panelTitle"><div>
        <h2>{t('settings.appearance')}</h2>
        <span>{t('settings.appearanceDesc')}</span>
      </div></div>
      <div className="settingsContent">
        <AppearanceControls />
        <p className="muted">{t('settings.preferencesSaved')}</p>
      </div>
    </section>
    <section className="panel settingsPanel">
      <div className="panelTitle"><div>
        <h2>{t('settings.version')}</h2>
        <span>{t('settings.versionDesc')}</span>
      </div></div>
      <div className="settingsContent settingsVersionRow">
        <strong className="versionPill">{productVersion ? 'v' + productVersion.replace(/^v/, '') : t('settings.versionUnknown')}</strong>
        <button className="secondary" onClick={onOpenReleases}>{t('nav.releases')}</button>
      </div>
    </section>
  </div>
}
