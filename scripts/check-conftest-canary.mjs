#!/usr/bin/env node
/*
 * Proves the conftest policy gate is actually evaluating rules.
 *
 * A conftest run that silently matches zero rules exits 0 and looks
 * identical to a genuinely clean plan -- that's exactly what happened
 * before conftest.toml pinned the namespace: `conftest test --policy
 * policy/terraform plan.json` reported "0 tests, 0 passed" and passed
 * every plan, compliant or not. A green CI check must not be trustable
 * on faith; this script runs conftest against a fixture that is known,
 * deliberately, to violate policy (SQL with public_network_access_enabled
 * = true) and fails the build unless conftest actually reports a failure
 * for it. If this canary ever goes green, the gate itself is broken.
 *
 * Usage: node scripts/check-conftest-canary.mjs
 * Exit:  0 the gate is alive (fixture failed as expected)
 *        1 the gate is broken (fixture passed, or conftest errored)
 */

import { spawnSync } from "node:child_process";

const POLICY_DIR = "policy/terraform";
const FIXTURE = "policy/terraform/testdata/non_compliant_plan.json";
const CONFTEST_BIN = process.env.CONFTEST_BIN ?? "conftest";

const result = spawnSync(
  CONFTEST_BIN,
  ["test", "--policy", POLICY_DIR, "--output", "json", FIXTURE],
  { encoding: "utf8" },
);

if (result.error) {
  console.error(`Could not run conftest: ${result.error.message}`);
  process.exit(1);
}

// conftest exits 1 whenever it records a failure, so a non-zero/non-one
// exit code (missing binary, a rego syntax error, a malformed fixture)
// is a different failure mode from the one this canary is checking for.
if (result.status !== 0 && result.status !== 1) {
  console.error(`conftest exited ${result.status} unexpectedly.`);
  console.error(result.stderr || result.stdout);
  process.exit(1);
}

let report;
try {
  report = JSON.parse(result.stdout);
} catch {
  console.error("Could not parse conftest --output json output:");
  console.error(result.stdout);
  console.error(result.stderr);
  process.exit(1);
}

const totalFailures = report.reduce((sum, file) => sum + (file.failures?.length ?? 0), 0);
const totalChecks = report.reduce(
  (sum, file) => sum + (file.successes ?? 0) + (file.failures?.length ?? 0) + (file.warnings?.length ?? 0),
  0,
);

if (totalChecks === 0) {
  console.error(
    `conftest evaluated zero rules against ${FIXTURE}. The policy namespace is not being ` +
      "matched (check conftest.toml / --namespace) -- the gate is not checking anything.",
  );
  process.exit(1);
}

if (totalFailures === 0) {
  console.error(
    `conftest reported zero failures against a fixture that is deliberately non-compliant ` +
      `(${FIXTURE} has public_network_access_enabled = true on an Azure SQL server). ` +
      "The policy gate is not catching known-bad input -- treat this as broken, not passing.",
  );
  process.exit(1);
}

console.log(
  `conftest canary OK: ${totalChecks} rule(s) evaluated, ${totalFailures} failure(s) ` +
    `correctly reported against the non-compliant fixture. The policy gate is alive.`,
);
