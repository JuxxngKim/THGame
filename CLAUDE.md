# CLAUDE.md

## 0. Language Requirement (MUST READ FIRST)

**All responses MUST be provided in Korean (한글).**

This applies to all explanations, comments, commit messages, code reviews, and any text output directed to the user. Code itself (variable names, function names, etc.) follows the coding style guide.

---

## 1. Build Method and Project Structure

- **Solution file**: xxx.sln
- **Build command**: [Insert build command here]

**Project structure**:
- `Server/` — Server source code
- `Client/` — UE5 client
- ...

---

## 2. General Precautions

- Only modify `protocol.proto` and `sprotocol.proto` for proto file changes
- All new files MUST be created with **UTF-8 with BOM** encoding

---

## 3. 🚨 DB Access Rules (Strictly Enforced, No Exceptions)

- **DO NOT** execute any data/schema-modifying queries (INSERT/UPDATE/DELETE/MERGE/TRUNCATE/DROP/ALTER/CREATE, etc.) directly against the DB via the MCP MSSQL server
- The DB may **only** be queried with read-only SELECT statements
- If data addition/modification/deletion or schema changes are required, **do not execute the query directly**. Instead, create a separate SQL script file (`.sql`) and provide it so the user can review and execute it manually
- When writing the script, always include:
  - Target DB (3-part naming)
  - Scope of impact
  - Rollback method (preferably with `BEGIN TRAN` / `ROLLBACK` examples)
- **No exceptions**: Never execute write/modify queries directly, even for reasons like "just a quick one-time check"

---

## 4. Coding and Behavior Guidelines (LLM Guidelines)

**Core principle**: These guidelines prioritize **safety and caution over speed**. (For very trivial tasks, exercise flexible judgment as appropriate.)

### 4.1. Think Before Coding

**Do not assume. Do not hide ambiguity. State trade-offs explicitly.**

- Before implementing, clearly state your assumptions. If anything is uncertain, ask first.
- When multiple interpretations are possible, do not arbitrarily pick one — present the available options.
- If a simpler approach exists, suggest it. Push back on requirements when necessary.
- If something is unclear, **stop coding** and ask precise questions about the confusing parts.

### 4.2. Simplicity First

**Write the minimum code required to solve the problem. Avoid speculative implementations.**

- Do not implement features that were not requested.
- Do not over-abstract for one-off code.
- Do not add "flexibility" or "configurability" that was not requested.
- Do not write exception handling for scenarios that cannot occur.
- If you wrote 200 lines for something that could be done in 50, rewrite it. (Any code a senior engineer would call "over-engineered" must be simplified.)

### 4.3. Surgical Changes

**Modify only what is necessary. Clean up only the mess you made.**

When editing existing code:
- Do not arbitrarily "improve" adjacent code, comments, or formatting.
- Do not refactor code that isn't broken.
- Follow the existing project code style, even if it differs from your preference.
- If you find unrelated dead code during edits, **mention it but do not delete it**.

Handling fallout from your own changes:
- Remove orphaned imports, variables, and functions that became unused **as a result of your edits**.
- Do not remove pre-existing dead code unless explicitly requested.

**Verification test**: Every changed line must directly connect to the user's request.

### 4.4. Goal-Driven Execution

**Define success criteria. Iterate until verified.**

Convert tasks into concrete, verifiable goals:
- "Add validation" → "Write tests for invalid inputs first, then make them pass"
- "Fix the bug" → "Write a test that reproduces the bug, then make it pass"
- "Refactor X" → "Confirm all tests pass both before and after refactoring"

For multi-step tasks, present a brief plan first: