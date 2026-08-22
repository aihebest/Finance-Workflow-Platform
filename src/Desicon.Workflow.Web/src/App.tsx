import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from "@azure/msal-react";
import { NavLink, Route, Routes } from "react-router-dom";
import { apiScopes } from "./auth/msal";
import { useIsFinance } from "./auth/roles";
import { Inbox } from "./pages/Inbox";
import { MyAdvances } from "./pages/MyAdvances";
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
          </div>

          <nav className="mx-auto flex max-w-5xl gap-5 overflow-x-auto px-4 pt-4 text-sm">
            <Tab to="/expenses/new">New Expense</Tab>
            <Tab to="/advances/new">New Cash Advance</Tab>
            <Tab to="/advances">My Advances</Tab>
            <Tab to="/">My Inbox</Tab>
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
            <Route path="/reports" element={<Reports />} />
          </Routes>
        </div>
      </AuthenticatedTemplate>
    </>
  );
}
