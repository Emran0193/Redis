# 09 — Soak Testing

`tests/LoadTests/SoakScenarios.cs` (`Category=Soak`):

- GET/SET loop, concurrent clients, connection churn, pub/sub storm, snapshot-during-writes, Gen2 growth heuristic

Default duration ~30s. Set `NOVADB_SOAK_HOURS` for long runs. Reports append to `TestResults/soak-report.md`.
