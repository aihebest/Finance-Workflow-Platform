import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from "@azure/msal-react";
import { NavLink, Route, Routes } from "react-router-dom";
import { apiScopes } from "./auth/msal";
import { Inbox } from "./pages/Inbox";
import { RequestDetail } from "./pages/RequestDetail";

function SignIn() {
  const { instance } = useMsal();

  return (
    <div className="mx-auto mt-24 max-w-sm rounded border border-gray-200 bg-white p-6 text-center">
      <h1 className="text-lg font-semibold text-gray-900">Desicon Finance Workflow</h1>
      <p className="mt-2 text-sm text-gray-600">Sign in with your Desicon account.</p>
      <button
        type="button"
        onClick={() => void instance.loginRedirect({ scopes: apiScopes })}
        className="mt-4 min-h-11 w-full rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800"
      >
        Sign in
      </button>
    </div>
  );
}

export function App() {
  return (
    <>
      <UnauthenticatedTemplate>
        <SignIn />
      </UnauthenticatedTemplate>

      <AuthenticatedTemplate>
        <div className="mx-auto max-w-3xl p-4">
          <nav className="mb-4 flex gap-4 border-b border-gray-200 pb-2">
            {/* Only the approval path is built so far. My Requests, My
                Advances and the capture forms follow; deliberately not stubbed
                with dead links, because a nav item that goes nowhere teaches
                people the app is broken. */}
            <NavLink
              to="/"
              className={({ isActive }) =>
                isActive
                  ? "border-b-2 border-blue-700 pb-2 font-medium text-blue-700"
                  : "pb-2 text-gray-600 hover:text-gray-900"
              }
            >
              My Inbox
            </NavLink>
          </nav>

          <Routes>
            <Route path="/" element={<Inbox />} />
            <Route path="/requests/:id" element={<RequestDetail />} />
          </Routes>
        </div>
      </AuthenticatedTemplate>
    </>
  );
}
