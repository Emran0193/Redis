# Replication

NovaDB is intentionally **single-node ephemeral cache**. There is no replica, Sentinel, or cluster runtime.

`INFO` reports `role:master` and `connected_slaves:0` for Redis-client compatibility only.

**Showcase framing:** lose the process → rebuild from origin (or AOF/snapshot if you enabled persistence for the demo). HA / replication is a separate future product decision — stubs that implied unfinished HA were removed so the project does not over-claim.
