/** Hands a file to the person: in the tab that the click opened (a PDF, which the browser shows), or as a download. */
export function save(blob: Blob, name: string, tab: Window | null = null) {
  const url = URL.createObjectURL(blob)
  if (tab) {
    // The tab was opened by the click itself (a window opened after the wait for the file is taken for a pop-up and blocked); it is told where to go now, cut from this page.
    tab.opener = null
    tab.location.href = url
  } else {
    const link = document.createElement('a')
    link.href = url
    link.download = name
    link.click()
  }
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
