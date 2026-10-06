# End-user E2E suite

The suite drives the real Docker image through HTTP. It does not use mocks or the EF in-memory provider.

Default gate:
- PostgreSQL and Redis health.
- WebAPI health.
- public login page.
- authentication redirect for protected admin pages.
- headless first-run admin credential generation and one-time bootstrap consumption.
- real admin login.
- Secret Vault create/list/reveal/rotate/delete.
- redaction: secret plaintext must never appear in list output.
- real PostgreSQL connection probe.
- real SQLite connection probe.
- encrypted config save.
- application restart and persistence/idempotency.

Optional SQL Server probe:
- --with-sqlserver starts a real SQL Server container and tests the config API against it.
- This does not claim full application SQL Server certification; provider-specific runtime SQL still needs its own certification matrix.

Run on Linux/macOS:
  ./tests/e2e/run.sh
  ./tests/e2e/run.sh --with-sqlserver

Run on Windows PowerShell 7:
  pwsh -File .\\tests\\e2e\\run.ps1
  pwsh -File .\\tests\\e2e\\run.ps1 -WithSqlServer
