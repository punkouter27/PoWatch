# AGENTS.md — PoWatch

Rules for any agent working in this repository.

### 1. Git & Workflow
* **Stay on `master`:** Commit directly to `master`. Create branches only when specifically asked.
* **Casual Commits:** Write short, natural commit messages using informal American phrasing. Auto-commit and push on sync.

### 2. Running, Testing & Clean Code
* **Run Only Related Tests:** Test only what was touched. Skip tests for minor edits; never run the whole test suite.
* **Zero Warnings:** Fix all build warnings immediately. Treat every warning as a failure.

### 3. Architecture & Setup
* **Keep Docs Updated:** Check `DOCS/` and `README.md` first to get an overview of app quickly (if they exist)
* **Fully Automated:** Run all builds, scripts, and setup commands directly. Do not ask the user to run terminal commands.

### 4. UI & Output Rules
* **UI Previews:** For visual changes, save before-and-after HTML mockups to `SCREENSHOTS/{YYMMDD}-{feature}.html` / Annotate screenshots and show in a single PNG the before and after with notes
* **Clarifying Questions:** If modifying more than 100 lines and no pending questions exist, list 3 key follow-up questions about future direction or logic.
* **Required Footers:**
  * **Short Recap:** If your response is over 100 words, add a recap under 20 words at the very end.
  * **3-Level Summary:** If a code change is over 20 lines, add 3 single-sentence summaries:
    * *Toddler:* Explain the idea simply.
    * *Child:* Explain what the app actually does now.
    * *Adult:* Explain how it was built.

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds, tests, deploys and health-gates (`.github/workflows/deploy.yml`). Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `SCRIPTS/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies) and `/health` (dependencies reachable). The deploy's health gate checks both.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
