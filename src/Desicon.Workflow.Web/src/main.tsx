import { MsalProvider } from "@azure/msal-react";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { App } from "./App";
import { msalInstance } from "./auth/msal";
import "./index.css";

/**
 * Bootstraps MSAL, then renders.
 *
 * Wrapped in a function rather than written as top-level await, which esbuild
 * rejects against the default browser targets. Raising the target would make
 * it compile and would drop older Safari -- and mobile Safari is a required
 * browser for the approval path, not an afterthought: a department head
 * approving from a phone on a site is the case this whole platform is trying
 * to make fast.
 *
 * Order matters. initialize() must complete before any other MSAL call in v3,
 * and handleRedirectPromise() must resolve before the app reads accounts --
 * otherwise the first render after a sign-in redirect sees no account and
 * sends the person straight back to the sign-in they just completed.
 */
async function bootstrap(): Promise<void> {
  await msalInstance.initialize();
  await msalInstance.handleRedirectPromise();

  const accounts = msalInstance.getAllAccounts();
  const first = accounts[0];

  if (first) {
    msalInstance.setActiveAccount(first);
  }

  const container = document.getElementById("root");

  if (!container) {
    throw new Error("Root element is missing from index.html.");
  }

  createRoot(container).render(
    <StrictMode>
      <MsalProvider instance={msalInstance}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </MsalProvider>
    </StrictMode>,
  );
}

void bootstrap();
