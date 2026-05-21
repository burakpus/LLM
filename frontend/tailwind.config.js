/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        bg:           'var(--bg)',
        surface:      'var(--surface)',
        'surface-hi': 'var(--surface-hi)',
        'surface-2':  'var(--surface-2)',
        border:       'var(--border)',
        accent:       'var(--accent)',
        'accent-hi':  'var(--accent-hi)',
        text:         'var(--text)',
        'text-2':     'var(--text-2)',
        mute:         'var(--mute)',
        'mute-2':     'var(--mute-2)',
        'user-bubble':'var(--user-bubble)',
        'input-bg':   'var(--input-bg)',
      },
      borderRadius: {
        'gemini': 'var(--radius-lg)',
      },
    },
  },
  plugins: [],
}
