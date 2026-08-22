/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    // Extended, not replaced. Core utilities only -- no arbitrary values --
    // so the build stays a plain Tailwind build with no JIT surprises.
    //
    // Brand colours are named here rather than written inline for the same
    // reason: a hex code repeated across twenty files is twenty places to
    // miss when it changes. Sampled from the logo artwork, not guessed.
    extend: {
      colors: {
        desicon: {
          // The wordmark's near-black navy, and a darker shade for the bar
          // above it. Deep enough that white type sits comfortably on it.
          navy: "#0B1F3A",
          deep: "#071628",

          // The two gradients in the 3D mark: cyan on the inner face, violet
          // on the outer. Cyan is the accent -- it is what the eye already
          // follows in the logo.
          cyan: "#18A8F0",
          sky: "#00C0F0",
          violet: "#300090",
        },
      },
    },
  },
  plugins: [],
};
