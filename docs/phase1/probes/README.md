# Phase 1 probe logs — the primary measurements

These are the raw outputs of the Phase 0/Phase 1 probes (`P0`–`P13`) and the fixture-builder
runs, moved here from the machine-local `phase0/` scratch folder when Phase 1 closed on
2026-08-17. Each `.raw.log` is the untouched stdout of one CODESYS run — every line starting
`@@VERSIO@@` is one JSON event — and each `.job.json` beside it is exactly what that run was
asked to do.

**Why they are committed at all**: every P-numbered claim in
[`../IMPORT-PLAN.md`](../IMPORT-PLAN.md) and several findings in
[`../../FINDINGS.md`](../../FINDINGS.md) summarize rows from these files, and one finding —
the P11 probe carrying its third lie — exists *because* a verdict row
(`OBJECT_NOT_AT_SOURCE`) sat unread in one of them. The summaries are in the documents; this
folder is the primary data, kept so a claim can be re-audited against what the toolchain
actually said rather than against what somebody wrote down about it.

**What was deliberately not kept**: the routine `versio_export` / `versio_import` harness logs
(212 files) and every scratch project copy — regenerable by rerunning the tools against the
committed fixtures. The tools (`tools/codesys/Probe-ImportApi.ps1`, `Run-Codesys.ps1`,
`Measure-ImportStability.ps1`) recreate their `phase0/` working folder on the next run; it
stays in `.gitignore`.

Timestamps in the file names are local time on the machine that ran them; the git log is the
authority on when anything landed.
