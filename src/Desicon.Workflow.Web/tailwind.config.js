/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    // Extended, not replaced. Core utilities only -- no arbitrary values --
    // so the build stays a plain Tailwind build with no JIT surprises.
    extend: {},
  },
  plugins: [],
};
