// =============================================================================
// auth-interceptor.ts
//
// Wraps window.fetch once at app boot. On 401 from /api/* (except the login
// endpoint itself), clears the JWT/user from localStorage and redirects to
// /login — so users with an expired session never see broken UI states.
//
// Login failures (invalid credentials) also return 401, so we *skip* the
// redirect for /api/auth/login specifically — that response is consumed by
// the login form normally.
// =============================================================================

let installed = false

export function installAuthInterceptor(): void {
  if (installed) return
  installed = true

  const originalFetch = window.fetch.bind(window)

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const resp = await originalFetch(input, init)

    if (resp.status !== 401) return resp

    // Extract URL string
    const url = typeof input === 'string'
      ? input
      : input instanceof URL ? input.toString()
      : input.url

    // Skip the login endpoint — its 401 means "wrong password", not "expired".
    if (url.includes('/api/auth/login')) return resp

    // Only react to our own API; ignore third-party 401s.
    if (!url.includes('/api/')) return resp

    // Avoid infinite redirect loops.
    if (window.location.pathname === '/login') return resp

    try {
      localStorage.removeItem('setllm-token')
      localStorage.removeItem('setllm-user')
    } catch { /* SSR / private mode — ignore */ }

    // Soft notice via console; UI banner would be nicer but requires hooking
    // into the store. /login page will show a normal sign-in form.
    console.warn('[auth] 401 received — session expired, redirecting to /login')

    // Preserve where the user was for an after-login bounce-back (future).
    const back = encodeURIComponent(window.location.pathname + window.location.search)
    window.location.href = `/login?expired=1&next=${back}`

    return resp
  }
}
