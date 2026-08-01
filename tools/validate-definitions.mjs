#!/usr/bin/env node
/*
 * Mirrors WorkflowDefinitionValidator so the JSON definitions can be checked
 * without a .NET toolchain. The C# validator remains authoritative and runs in
 * CI; this exists for fast local feedback and for editors without .NET.
 *
 * Usage: node tools/validate-definitions.mjs [dir]
 */

import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const KNOWN_RESOLVERS = new Set([
  "Requester",
  "Beneficiary",
  "LineManagerOf",
  "DepartmentHeadOf",
  "ProjectManagerOf",
  "CurrentActor",
]);

const NEGATIVE_ACTIONS = new Set(["REJECT", "RETURN", "WRITE_OFF"]);

function traverse(def, startKey) {
  const visited = new Set([startKey]);
  const queue = [startKey];

  while (queue.length > 0) {
    const current = queue.shift();
    for (const t of def.transitions.filter((x) => x.from === current)) {
      if (!visited.has(t.to)) {
        visited.add(t.to);
        queue.push(t.to);
      }
    }
  }

  return visited;
}

function validate(def) {
  const errors = [];
  const warnings = [];
  const stateKeys = new Set();

  for (const s of def.states) {
    if (stateKeys.has(s.key)) errors.push(`DUPLICATE_STATE: ${s.key}`);
    stateKeys.add(s.key);
  }

  const initial = def.states.filter((s) => s.type === "initial");
  if (initial.length === 0) errors.push("NO_INITIAL_STATE");
  if (initial.length > 1)
    errors.push(`MULTIPLE_INITIAL_STATES: ${initial.map((s) => s.key).join(", ")}`);

  const terminals = def.states.filter((s) => s.type === "terminal").map((s) => s.key);
  if (terminals.length === 0) errors.push("NO_TERMINAL_STATE");

  for (const key of terminals) {
    if (def.transitions.some((t) => t.from === key))
      errors.push(`TERMINAL_HAS_EXIT: ${key}`);
  }

  for (const t of def.transitions) {
    if (!stateKeys.has(t.from)) errors.push(`UNKNOWN_FROM_STATE: ${t.from} (${t.action})`);
    if (!stateKeys.has(t.to)) errors.push(`UNKNOWN_TO_STATE: ${t.to} (${t.action})`);
    if (!t.action) errors.push(`MISSING_ACTION: ${t.from} -> ${t.to}`);

    if (!t.actor || (!t.actor.role && !t.actor.resolver))
      errors.push(`NO_ACTOR: ${t.action} from ${t.from}`);

    if (t.actor?.resolver && !KNOWN_RESOLVERS.has(t.actor.resolver))
      errors.push(`UNKNOWN_RESOLVER: ${t.actor.resolver} (${t.action})`);

    if (t.guard && !t.guardMessage)
      warnings.push(`GUARD_WITHOUT_MESSAGE: ${t.action} from ${t.from}`);
  }

  for (const s of def.states) {
    if (!s.sla) {
      if (!s.type || s.type === "normal")
        warnings.push(`STATE_WITHOUT_SLA: ${s.key}`);
      continue;
    }

    if (s.sla.hours <= 0) errors.push(`INVALID_SLA: ${s.key}`);

    if (s.sla.escalateTo && !stateKeys.has(s.sla.escalateTo))
      errors.push(`UNKNOWN_ESCALATION_TARGET: ${s.key} -> ${s.sla.escalateTo}`);

    if (s.sla.reminderEveryHours && s.sla.reminderEveryHours >= s.sla.hours)
      warnings.push(
        `REMINDER_AFTER_BREACH: ${s.key} reminds at ${s.sla.reminderEveryHours}h but breaches at ${s.sla.hours}h`,
      );
  }

  if (initial.length === 1) {
    const reachable = traverse(def, initial[0].key);
    for (const s of def.states)
      if (!reachable.has(s.key)) errors.push(`UNREACHABLE_STATE: ${s.key}`);
  }

  const terminalSet = new Set(terminals);
  for (const s of def.states.filter((x) => x.type !== "terminal")) {
    const reachable = traverse(def, s.key);
    if (![...reachable].some((k) => terminalSet.has(k)))
      errors.push(`DEAD_END_STATE: ${s.key} can never be closed`);
  }

  for (const s of def.states.filter((x) => x.sla && (!x.type || x.type === "normal"))) {
    const outbound = def.transitions.filter((t) => t.from === s.key);
    if (outbound.length > 0 && !outbound.some((t) => NEGATIVE_ACTIONS.has(t.action)))
      warnings.push(`NO_NEGATIVE_PATH: ${s.key} offers no REJECT or RETURN`);
  }

  return { errors, warnings };
}

const dir = process.argv[2] ?? "modules";
const files = readdirSync(dir).filter((f) => f.endsWith(".workflow.json"));

let failed = false;

for (const file of files) {
  const def = JSON.parse(readFileSync(join(dir, file), "utf8"));
  const { errors, warnings } = validate(def);

  console.log(`\n${file}  (${def.moduleKey} — ${def.formCode} Rev ${def.formRevision})`);
  console.log(`  states: ${def.states.length}  transitions: ${def.transitions.length}`);

  for (const w of warnings) console.log(`  WARN   ${w}`);
  for (const e of errors) console.log(`  ERROR  ${e}`);

  if (errors.length === 0) console.log("  OK — no structural errors");
  if (errors.length > 0) failed = true;
}

console.log("");
process.exit(failed ? 1 : 0);
