#!/usr/bin/env node
/*
 * Enforces the security exception register.
 *
 * A suppression with no deadline is not an exception, it is permanent risk
 * acceptance by whoever happened to be in a hurry. This script makes the
 * deadline real: an expired exception fails the build, which forces the
 * conversation to happen again with the people who own the risk.
 *
 * Checks:
 *   - required fields present
 *   - expiry in the future
 *   - Critical/High capped at 30 days, others at 180
 *   - a linked issue for anything above low severity
 *
 * Usage: node scripts/check-exceptions.mjs [file]
 * Exit:  0 clean, 1 violations found
 */

import { readFileSync, existsSync } from "node:fs";

const FILE = process.argv[2] ?? "security-exceptions.yml";

const MAX_DAYS = { critical: 30, high: 30, medium: 180, low: 180 };
const REQUIRED = [
  "id",
  "tool",
  "rule",
  "scope",
  "severity",
  "justification",
  "owner",
  "accepted_on",
  "expires_on",
];

/*
 * Deliberately a tiny hand-rolled reader rather than a YAML dependency. This
 * script gates the security pipeline, so it should not itself pull a package
 * from the registry it is helping to police -- and the file's shape is fixed.
 */
function parseExceptions(text) {
  const lines = text.split("\n");
  const entries = [];
  let current = null;
  let continuationKey = null;

  for (const raw of lines) {
    const line = raw.replace(/\t/g, "  ");
    if (!line.trim() || line.trim().startsWith("#")) continue;

    const itemMatch = line.match(/^\s*-\s+(\w+):\s*(.*)$/);
    if (itemMatch) {
      if (current) entries.push(current);
      current = { __line: lines.indexOf(raw) + 1 };
      current[itemMatch[1]] = stripFold(itemMatch[2]);
      continuationKey = itemMatch[2].trim() === ">" ? itemMatch[1] : null;
      continue;
    }

    const kvMatch = line.match(/^\s{4,}(\w+):\s*(.*)$/);
    if (kvMatch && current) {
      const [, key, value] = kvMatch;
      current[key] = stripFold(value);
      continuationKey = value.trim() === ">" || value.trim() === "|" ? key : null;
      continue;
    }

    if (continuationKey && current && /^\s{6,}\S/.test(line)) {
      current[continuationKey] = `${current[continuationKey] ?? ""} ${line.trim()}`.trim();
    }
  }

  if (current) entries.push(current);
  return entries.filter((e) => e.id);
}

const stripFold = (v) => {
  const t = v.trim();
  if (t === ">" || t === "|") return "";
  return t.replace(/^["']|["']$/g, "");
};

const days = (from, to) => Math.round((to - from) / 86_400_000);

if (!existsSync(FILE)) {
  console.log(`No exception register at ${FILE} — nothing to check.`);
  process.exit(0);
}

const entries = parseExceptions(readFileSync(FILE, "utf8"));
const today = new Date(new Date().toISOString().slice(0, 10));

const errors = [];
const warnings = [];

console.log(`Security exception register: ${entries.length} active entr${entries.length === 1 ? "y" : "ies"}\n`);

for (const e of entries) {
  const id = e.id ?? "(no id)";

  for (const field of REQUIRED) {
    if (!e[field]) errors.push(`${id}: missing required field '${field}'`);
  }

  const severity = (e.severity ?? "").toLowerCase();
  if (severity && !MAX_DAYS[severity]) {
    errors.push(`${id}: unknown severity '${e.severity}'`);
  }

  if (!e.expires_on) continue;

  const expires = new Date(e.expires_on);
  if (Number.isNaN(expires.getTime())) {
    errors.push(`${id}: expires_on '${e.expires_on}' is not a date`);
    continue;
  }

  const remaining = days(today, expires);

  if (remaining < 0) {
    errors.push(
      `${id}: EXPIRED ${Math.abs(remaining)} day(s) ago (${e.expires_on}). ` +
        `Fix the finding or have ${e.owner ?? "the owner"} renew it with a fresh justification.`,
    );
  } else if (remaining <= 14) {
    warnings.push(`${id}: expires in ${remaining} day(s) on ${e.expires_on}`);
  }

  if (e.accepted_on) {
    const accepted = new Date(e.accepted_on);
    const window = days(accepted, expires);
    const cap = MAX_DAYS[severity] ?? 180;

    if (window > cap) {
      errors.push(
        `${id}: ${severity} exception granted for ${window} days, which exceeds the ${cap}-day cap.`,
      );
    }
  }

  if (severity !== "low" && !e.issue) {
    errors.push(`${id}: ${severity} exceptions must link a tracking issue.`);
  }

  const status = remaining < 0 ? "EXPIRED" : `${remaining}d left`;
  console.log(`  ${id.padEnd(12)} ${severity.padEnd(9)} ${String(e.tool).padEnd(18)} ${status}`);
}

if (warnings.length) {
  console.log("\nExpiring soon:");
  for (const w of warnings) console.log(`  WARN   ${w}`);
}

if (errors.length) {
  console.log("\nViolations:");
  for (const e of errors) console.log(`  ERROR  ${e}`);
  console.log(`\n${errors.length} violation(s). Failing the build.`);
  process.exit(1);
}

console.log("\nAll exceptions valid and within their windows.");
