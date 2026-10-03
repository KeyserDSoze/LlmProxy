import Governance from './Governance'
import PageDocumentation from './PageDocumentation'

export default function GovernancePage() {
  return <div className="shell">
    <aside className="sidebar">
      <div className="brand"><div className="brandMark">LP</div><div><strong>LlmProxy</strong><span>AI Gateway</span></div></div>
      <nav>
        <button onClick={() => { window.location.href = '/admin/' }}>Dashboard</button>
        <button className="active">Usage & Governance</button>
      </nav>
      <div className="sidebarFooter"><span className="dot" /> OpenAI-compatible gateway</div>
    </aside>
    <main>
      <header>
        <div><h1>Usage & Governance</h1><p>Authentication, groups, caller rate limits and consolidated usage</p></div>
        <button className="secondary" onClick={() => { window.location.href = '/admin/' }}>Back to gateway</button>
      </header>
      <PageDocumentation page="governance" />
      <Governance />
    </main>
  </div>
}
