/**
 * Horizontally scrollable tables must be reachable from the keyboard (WCAG
 * 2.1.1, axe `scrollable-region-focusable`). Data tables scroll inside a
 * `.tableScroll` wrapper or their panel; whichever element actually overflows
 * becomes a focusable, labelled region and reverts once its content fits.
 */
const candidates = '.tableScroll, .panel'

function regionLabel(element: HTMLElement) {
  const heading = (element.matches('.panel') ? element : element.closest('.panel'))?.querySelector('h2, h3')
  return heading?.textContent?.trim() || 'Scrollable table'
}

function update(element: HTMLElement) {
  const overflowX = getComputedStyle(element).overflowX
  const scrollable = (overflowX === 'auto' || overflowX === 'scroll') && element.scrollWidth > element.clientWidth + 1
  const managed = element.dataset.scrollRegion
  if (scrollable && managed === undefined && !element.hasAttribute('tabindex')) {
    // Remember exactly which attributes were added so reverting never strips authored ones.
    const added = ['tabindex']
    element.tabIndex = 0
    if (!element.hasAttribute('role')) { element.setAttribute('role', 'region'); added.push('role') }
    if (!element.hasAttribute('aria-label')) { element.setAttribute('aria-label', regionLabel(element)); added.push('aria-label') }
    element.dataset.scrollRegion = added.join(' ')
  } else if (!scrollable && managed !== undefined) {
    for (const name of managed.split(' ')) element.removeAttribute(name)
    delete element.dataset.scrollRegion
  }
}

export function enhanceScrollableRegions(root: Document) {
  if (typeof MutationObserver === 'undefined' || typeof requestAnimationFrame === 'undefined') return
  let scheduled = false
  const scan = () => {
    scheduled = false
    root.querySelectorAll<HTMLElement>(candidates).forEach(update)
  }
  const schedule = () => {
    if (scheduled) return
    scheduled = true
    requestAnimationFrame(scan)
  }
  new MutationObserver(schedule).observe(root.body, { childList: true, subtree: true })
  root.defaultView?.addEventListener('resize', schedule)
  schedule()
}
