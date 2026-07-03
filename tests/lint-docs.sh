#!/usr/bin/env bash
# Docs lint: keep typographic Unicode out of human-facing text.
#
# Em dashes, arrows, checkmarks and friends read as machine-generated boilerplate and turn
# readers off (and they creep back in easily). This gate fails CI when any tracked Markdown
# file contains one. ASCII equivalents to use instead:
#   em/en dash, minus  ->  "-"        arrows       ->  "->" / "<->"
#   approx             ->  "~"        multiply     ->  "x"
#   <= / >= signs      ->  "<=" ">="  ellipsis     ->  "..."
#   check / cross      ->  words      element-of   ->  "in"
#   curly quotes       ->  straight quotes
# Deliberately allowed: +/- sign, section sign, accented letters (real names).
set -euo pipefail
cd "$(dirname "$0")/.."

PATTERN=$'[—–−→←⇄≈×≥≤✓✗∧…≡√¾∈‘’“”]'

if git ls-files '*.md' | xargs grep -nP "$PATTERN" 2>/dev/null; then
    echo "DOCS LINT: non-ASCII typography found (see lines above); use the ASCII forms." >&2
    exit 1
fi
echo "DOCS LINT: clean."
