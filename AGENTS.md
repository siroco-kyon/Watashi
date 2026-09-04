# Repository working notes

## Keep documentation synchronized

Any change to behavior, UI, APIs, configuration, deployment, supported runtimes, or operational procedures must update the affected documentation in the same pull request.

- Treat `README.md`, `docs/SPECIFICATION.md`, `docs/FEATURES.md`, `docs/USER-GUIDE.md`, `docs/ADMIN-GUIDE.md`, and `docs/CHANGELOG.md` as the canonical Markdown set.
- Keep the reader-facing HTML counterparts in sync: `docs/spec/index.html`, `docs/manual/index.html`, `docs/manual/admin.html`, and `docs/SETUP.html` when their corresponding behavior changes.
- Verify claims against the current implementation and configuration; do not copy obsolete behavior from plans or old changelog entries.
- Search the full documentation set for superseded terms and contradictory guidance before merging.
- Update `docs/CHANGELOG.md` for user-visible or operator-visible changes.
- Documentation-only pull requests should still run the repository's documentation consistency checks and the normal test suite when practical.
