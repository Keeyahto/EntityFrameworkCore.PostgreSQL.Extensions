# Agent instructions

Work on the user's requested scope. Read the [README](README.md) and the relevant current guide before editing.

- For code, dependencies, tests or packaging, follow [Code changes](.agents/code.md).
- For documentation, examples or the website, follow [Documentation changes](.agents/documentation.md).
- Code changes that affect usage or behavior must update the English and Russian documentation in the same change.
- Keep the library independent of private consumers. Preserve caller-owned transactions, explicit failures and native PostgreSQL behavior.
- Add only task deliverables to Git. Unrequested reports, evidence bundles, review transcripts, plans, logs and agent notes stay outside version control. Use Git history for superseded documents.
- Run the relevant checks and report actual results and remaining limitations in the response. Publishing packages, deploying the site and changing release policy require explicit user authorization.
