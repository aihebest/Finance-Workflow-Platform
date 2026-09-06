import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from "@azure/msal-react";
import { NavLink, Route, Routes } from "react-router";
import { apiScopes } from "./auth/msal";
import { useIsFinance } from "./auth/roles";
import { Inbox } from "./pages/Inbox";
import { MyAdvances } from "./pages/MyAdvances";
import { MyApprovals } from "./pages/MyApprovals";
import { NewCashAdvance } from "./pages/NewCashAdvance";
import { NewExpense } from "./pages/NewExpense";
import { Reports } from "./pages/Reports";
import { RequestDetail } from "./pages/RequestDetail";

/**
 * Desicon livery.
 *
 * Colour is confined to the chrome. The bar carries the brand; the page below
 * it stays near-paper and the forms stay plain, because DEL-AC-FRM-002 is
 * recognisable to a clerk who has filled it for eight years and that
 * recognisability is the requirement, not a preference. A branded form would
 * be a worse form.
 *
 * The wordmark is near-black and would disappear on a dark bar, so the chrome
 * uses the 3D mark with white type beside it. The full lockup appears on the
 * sign-in card, which is on white where it was designed to sit.
 */

function SignIn() {
  const { instance } = useMsal();

  return (
    <div className="flex min-h-screen flex-col items-center justify-center bg-gray-50 px-4">
      <div className="w-full max-w-sm rounded-lg border border-gray-200 bg-white p-8 text-center shadow-sm">
        <img
          src="/desicon-logo.png"
          alt="Desicon — Engineering, Innovation, Excellence"
          className="mx-auto h-14 w-auto"
        />

        <h1 className="mt-6 text-lg font-semibold text-desicon-navy">Finance Workflow</h1>
        <p className="mt-1 text-sm text-gray-600">
          Expense claims and cash advances. Sign in with your Desicon account.
        </p>

        <button
          type="button"
          onClick={() => void instance.loginRedirect({ scopes: apiScopes })}
          className="mt-6 min-h-11 w-full rounded bg-desicon-navy px-4 py-2 font-medium text-white hover:bg-desicon-deep"
        >
          Sign in
        </button>
      </div>

      <p className="mt-6 text-xs text-gray-500">DEL-AC-FRM-002 · DEL-AC-FRM-003</p>
    </div>
  );
}

/**
 * Who is signed in, and the way out.
 *
 * Neither existed until 6 September 2026. Asked for by Aihe, looking at a live
 * request and noticing there was nowhere to sign out — "do anyone login need
 * where to logout or you just close the browser page?"
 *
 * Closing the page was the only way out, and it is not one. The token cache is
 * sessionStorage, so closing the tab does clear this application's copy. The
 * Entra sign-in cookie lives in the browser, not the tab, and survives. The
 * next person to open finance.desiconapp.com on that machine is signed straight
 * back in as the last one, silently, because that is precisely what single
 * sign-on is for. On a shared site machine — the normal case for a Nigerian
 * project office, and the reason the cache is sessionStorage in the first
 * place — the second person raises requests as the first.
 *
 * `postLogoutRedirectUri` has been configured in auth/msal.ts since the first
 * release. The configuration for signing out was there. The button was not.
 *
 * WHY THE NAME IS HERE TOO
 * ------------------------
 * The header never said who was signed in, so nothing on screen could have let
 * anyone notice they were somebody else. Both the account's display name and
 * its username are shown, because §3e of the go-live checklist was written
 * after a claim was paid to the wrong one of two employees sharing a display
 * name: a name is not an identifier.
 *
 * WHY logoutRedirect AND NOT clearCache
 * -------------------------------------
 * Clearing the local cache alone would leave the Entra session intact, so the
 * next click of Sign in would walk straight back in without a prompt — the
 * exact behaviour this is here to stop. `logoutRedirect` ends the session at
 * the identity provider, scoped to this account, so the machine is genuinely
 * handed over. It also signs this account out of other Microsoft sessions in
 * the same browser, which is the intended trade on a shared machine and worth
 * knowing about on a personal one.
 */
function SignedInAs() {
  const { instance, accounts } = useMsal();
  const account = instance.getActiveAccount() ?? accounts[0];

  if (!account) {
    return null;
  }

  return (
    <div className="ml-auto flex items-center gap-3">
      <div className="hidden text-right sm:block">
        <div className="text-sm leading-tight text-white">{account.name ?? account.username}</div>
        {account.name && (
          <div className="text-xs leading-tight text-blue-200">{account.username}</div>
        )}
      </div>

      <button
        type="button"
        onClick={() => void instance.logoutRedirect({ account })}
        className="min-h-9 rounded border border-blue-300/60 px-3 py-1 text-sm text-blue-100 hover:bg-white/10 hover:text-white"
      >
        Sign out
      </button>
    </div>
  );
}

function Tab({ to, children }: { to: string; children: React.ReactNode }) {
  return (
    <NavLink
      to={to}
      end={to === "/"}
      className={({ isActive }) =>
        isActive
          ? "border-b-2 border-desicon-cyan pb-3 pt-1 font-medium text-white"
          : "border-b-2 border-transparent pb-3 pt-1 text-blue-100 hover:text-white"
      }
    >
      {children}
    </NavLink>
  );
}

export function App() {
  // Presentation only. The refusal that matters is the 403 in
  // ReportEndpoints -- hiding a tab hides nothing from anyone who types the
  // URL, and the route below is registered either way so that someone who
  // does gets the API's reason rather than a blank page.
  const isFinance = useIsFinance();

  return (
    <>
      <UnauthenticatedTemplate>
        <SignIn />
      </UnauthenticatedTemplate>

      <AuthenticatedTemplate>
        <header className="bg-desicon-navy">
          <div className="mx-auto flex max-w-5xl items-center gap-3 px-4 pt-4">
            <img src="/desicon-mark.png" alt="Desicon" className="h-8 w-auto" />
            <div>
              <div className="text-sm font-semibold leading-tight text-white">Desicon</div>
              <div className="text-xs leading-tight text-blue-200">Finance Workflow</div>
            </div>

            <SignedInAs />
          </div>

          <nav className="mx-auto flex max-w-5xl gap-5 overflow-x-auto px-4 pt-4 text-sm">
            <Tab to="/expenses/new">New Expense</Tab>
            <Tab to="/advances/new">New Cash Advance</Tab>
            <Tab to="/advances">My Advances</Tab>
            <Tab to="/">My Inbox</Tab>
            <Tab to="/approvals">My Approvals</Tab>
            {isFinance && <Tab to="/reports">Reports</Tab>}
          </nav>
        </header>

        {/* Near-paper, not white. The forms below sit on it the way they sit
            on a desk, and the chrome above is the only place colour lives. */}
        <div className="mx-auto max-w-5xl p-4">
          <Routes>
            <Route path="/" element={<Inbox />} />
            <Route path="/requests/:id" element={<RequestDetail />} />
            <Route path="/expenses/new" element={<NewExpense />} />
            <Route path="/advances/new" element={<NewCashAdvance />} />
            <Route path="/advances" element={<MyAdvances />} />
            <Route path="/approvals" element={<MyApprovals />} />
            <Route path="/reports" element={<Reports />} />
          </Routes>
        </div>
      </AuthenticatedTemplate>
    </>
  );
}
