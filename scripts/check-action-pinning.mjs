#!/usr/bin/env node
/*
 * Verifies every GitHub Action is pinned to a full 40-character commit SHA.
 *
 * A tag is a mutable pointer. `actions/checkout@v4` resolves to whatever the
 * owner of that tag last pushed, so a compromised or simply careless tag move
 * silently changes what runs inside a workflow that holds an OIDC token for
 * your Azure subscription. Tag-pinning is the supply-chain equivalent of
 * `latest`.
 *
 * SHA pinning is only half of it -- pinned actions go stale, which is why
 * .github/dependabot.yml tracks github-actions and raises PRs to move the pins.
 *
 * To pin an existing workflow:  npx ratchet pin .github/workflows/*.yml
 *
 * Usage: node scripts/check-action-pinning.mjs [dir]
 * Exit:  0 all pinned, 1 unpinned actions found
 */

import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

const DIR = process.argv[2] ?? ".github/workflows";

// Actions published by GitHub itself under the reusable-workflow mechanism
// cannot be SHA-pinned in the same way; none are currently in use, but the
// allowlist exists so an exemption is explicit rather than a silent skip.
const ALLOWLIST = new Set([]);

const SHA = /^[0-9a-f]{40}$/;
const USES = /^\s*-?\s*uses:\s*([^\s#]+)(?:\s*#\s*(.*))?/;

let unpinned = 0;
let pinned = 0;
let local = 0;

const files = readdirSync(DIR).filter((f) => /\.ya?ml$/.test(f));

for (const file of files) {
  const lines = readFileSync(join(DIR, file), "utf8").split("\n");
  const problems = [];

  lines.forEach((line, i) => {
    const match = line.match(USES);
    if (!match) return;

    const ref = match[1].trim();

    // Local composite actions and reusable workflows in this repo are covered
    // by branch protection, so they need no pin.
    if (ref.startsWith("./") || ref.startsWith(".github/")) {
      local++;
      return;
    }

    if (ALLOWLIST.has(ref.split("@")[0])) return;

    const [action, version] = ref.split("@");

    if (!version) {
      problems.push(`  line ${i + 1}: ${ref} — no ref at all`);
      unpinned++;
      return;
    }

    if (SHA.test(version)) {
      pinned++;
      if (!match[2]) {
        // Not fatal, but a bare SHA is unreadable in review.
        problems.push(
          `  line ${i + 1}: ${action} pinned but no version comment — add "# v4.2.2"`,
        );
      }
      return;
    }

    problems.push(
      `  line ${i + 1}: ${action}@${version} — pin to a full commit SHA`,
    );
    unpinned++;
  });

  if (problems.length) {
    console.log(`\n${file}`);
    problems.forEach((p) => console.log(p));
  }
}

console.log(
  `\n${pinned} pinned, ${unpinned} unpinned, ${local} local action reference(s).`,
);

if (unpinned > 0) {
  console.log(
    "\nUnpinned actions found. Run: npx ratchet pin .github/workflows/*.yml",
  );
  process.exit(1);
}

console.log("All external actions are pinned to a commit SHA.");
