import { useT, useUiPreferences } from './uiPreferences'
import type { UiLocale, UiTheme } from './uiPreferences'

export default function AppearanceControls({ compact = false }: { compact?: boolean }) {
  const t = useT()
  const { locale, theme, setLocale, setTheme } = useUiPreferences()
  return <div className={compact ? 'appearanceControls compact' : 'appearanceControls'}>
    <label className="appearanceField"><span>{t('common.language')}</span>
      <select aria-label={t('common.language')} value={locale} onChange={event => setLocale(event.target.value as UiLocale)}>
        <option value="en">{t('locale.en')}</option><option value="it">{t('locale.it')}</option>
      </select>
    </label>
    <label className="appearanceField"><span>{t('common.theme')}</span>
      <select aria-label={t('common.theme')} value={theme} onChange={event => setTheme(event.target.value as UiTheme)}>
        <option value="system">{t('theme.system')}</option><option value="light">{t('theme.light')}</option><option value="dark">{t('theme.dark')}</option>
      </select>
    </label>
  </div>
}
