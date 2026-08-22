import {
  PublicClientApplication,
  type Configuration,
  type SilentRequest,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";

function required(name: string): string {
  const value = import.meta.env[name as keyof ImportMetaEnv] as string | undefined;

  if (!value) {
    // Fail at startup with the missing variable named, rather than at first
    // sign-in with an opaque MSAL error. The API learned this lesson the
    // expensive way: a missing connection string fell back to a default and
    // failed on first query instead of at boot.
    throw new Error(
      `${name} is not configured. Copy .env.example to .env.local and fill it in.`,
    );
  }

  return value;
}

const configuration: Configuration = {
  auth: {
    clientId: required("VITE_ENTRA_CLIENT_ID"),
    authority: `https://login.microsoftonline.com/${required("VITE_ENTRA_TENANT_ID")}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,

    // Authorisation code flow with PKCE is the only flow msal-browser
    // performs, and the only one that should be used here. Implicit flow
    // would put a token in the URL fragment, where it lands in browser
    // history and in any referrer.
    //
    // navigateToLoginRequestUrl was set here, to true. It is no longer a
    // configuration option in msal-browser v5 -- it moved to an argument of
    // handleRedirectPromise(), where it still defaults to true. The
    // behaviour is unchanged, which matters more than the diff suggests:
    // it is what returns a department head to /requests/{id} after signing
    // in, instead of landing them on the inbox to find the claim again.
    // Every approval notification this platform sends is a deep link, so
    // this setting is the difference between one click and a hunt.
  },
  cache: {
    // sessionStorage, not localStorage. A token in localStorage outlives the
    // browser session and is readable by any script on the origin; on a shared
    // site machine — which is the normal case for a Nigerian project office —
    // it also means the next person to open the browser is still signed in as
    // the last one. The cost is re-authenticating on a new tab, which is
    // silent when the Entra session is still live.
    cacheLocation: "sessionStorage",

    // storeAuthStateInCookie was set here, to false. Removed in msal-browser
    // v5: it existed to carry auth state through IE11 and legacy Edge, which
    // the library no longer supports. False was already the default, so
    // nothing about this application changes.
  },
};

export const msalInstance = new PublicClientApplication(configuration);

/**
 * Scope the SPA requests for the API. Not `.default`: that asks for every
 * permission the app registration has ever been granted, which is the wrong
 * shape for a delegated user token.
 */
export const apiScopes = [required("VITE_API_SCOPE")];

/**
 * Acquires an access token for the API, falling back to a redirect when the
 * user must interact — consent, MFA, an expired session.
 *
 * Returns null rather than throwing when interaction has been triggered: the
 * browser is navigating away, so the caller has nothing useful left to do and
 * an exception here would surface as a spurious error toast on the way out.
 */
export async function acquireApiToken(): Promise<string | null> {
  const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0];

  if (!account) {
    await msalInstance.loginRedirect({ scopes: apiScopes });
    return null;
  }

  const request: SilentRequest = { scopes: apiScopes, account };

  try {
    const result = await msalInstance.acquireTokenSilent(request);
    return result.accessToken;
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect(request);
      return null;
    }

    throw error;
  }
}
