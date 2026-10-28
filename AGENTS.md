# AGENTS.md — PoWatch

Rules for any agent working in this repository.

- **Branching.** Do all work on `master`. Use another branch only when specifically asked to.
- **Restart after changes.** After any code change, restart the app and verify it comes back up successfully.
- **Project overview.** Check the `docs/` folder in the repo root for an overall summary of the project.
- **Configuration and secrets.** Do not use `dotnet user-secrets` to store data locally. Put it in
  `appsettings*.json`, or in Azure Key Vault if one exists.
- **Git sync.** When syncing, create a short commit message in casual American slang that reads like
  a human wrote it (not technical), then push.
- **TL;DR.** Any answer longer than 100 words ends with a TL;DR summary of about 20 words.
- **Three-level TL;DR for code changes.** When a code change is over 20 lines, summarize it at three
  levels: (1) Toddler, (2) Child, (3) Adult.
- **Tests.** Do not run the full test suite after code changes. Run only the tests related to the
  change, or none at all if the change is simple.
- **Automate, don't delegate.** Avoid asking the user to type CLI commands or click through a web
  GUI when you can do it yourself.
- **Warnings are errors.** Treat compile warnings as errors and fix them.
- **Big deletions.** If a prompt removes more than 100 lines of code overall, mention it.
- **UI screenshots.** When the UI changes, take annotated screenshots of the old and new UI with the
  changes marked. Put them in an HTML file in the `SCREENSHOTS` folder and give its valid full path.

## CI/CD and Azure (same section in every Po repo)

- A push to `master` builds, tests, deploys and health-gates (`.github/workflows/deploy.yml`). Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `SCRIPTS/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies) and `/health` (dependencies reachable). The deploy's health gate checks both.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.
