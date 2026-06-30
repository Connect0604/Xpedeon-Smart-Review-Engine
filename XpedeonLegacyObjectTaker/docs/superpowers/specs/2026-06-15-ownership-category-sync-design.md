# Phase 2: Ownership Category Sync — Design

## Goal

Phase 1 (`list_folders.bat` / `xpedeon-migration-form-sync` skill) inserts new forms
into `MIG.FORM` and scans each new form's source folder (Tables, Stored Procedures,
BLL, DAL, Form) to populate `MIG.OBJECT_OWNERSHIP` with one row per object,
defaulting `OwnershipCategory='LEGACY'` (DATABASE layer) or `'RETIRING'`
(BLL/DAL/UI layers), and `Remarks='Category update pending'`.

Phase 2 resolves that "pending" state for DATABASE-layer objects by checking
whether/how each table or stored procedure is actually used in the new Blazor
codebase at `D:\XpedeonSaas`, and updates `OwnershipCategory` accordingly.

## Scope

- Targets: `MIG.OBJECT_OWNERSHIP` rows WHERE `Layer='DATABASE'` AND
  `Remarks='Category update pending'`.
- As of 2026-06-15: 92 rows (60 PROC + 32 TABLE) across 6 forms — ApproverRoles,
  ContractPayOthElements, CostHeadAndCode, CVRGroups, PaymentTerms, SubledgerType.
- BLL/DAL/UI rows (old WinForms `.cs` files) are out of scope — they have no
  equivalent in the Blazor codebase and stay `RETIRING`.
- Allowed `OwnershipCategory` values (per `CK_MIG_OBJECT_Category`): `LEGACY`,
  `RETIRING`, `SHARED`, `BLAZOR_OWNED`.

## Rules

### TABLE rows (32)

Always set `OwnershipCategory = 'BLAZOR_OWNED'` (unconditional — Blazor's EF
data model targets the existing physical tables regardless of legacy usage).

For traceability, grep `D:\XpedeonSaas\**\SharedModels\Configuration\*.cs` for
`ToTable("<TableName>"` (case-insensitive):

- Match found → `Remarks = 'BLAZOR_OWNED: ToTable match in <ConfigFile>'`
- No match → `Remarks = 'BLAZOR_OWNED: no EF ToTable match found - verify mapping'`

### PROC rows (60)

Grep `D:\XpedeonSaas\**\Grpc\Services\DataProvider\*.cs` for `<ProcName>`
(case-insensitive). Skip matches on lines that are pure comments (trimmed line
starts with `//`, `*`, or `///`) — e.g. `SPN_AC_GET_APPROVER_ROLES` appears only
in a "... retired" comment and doesn't count as usage.

- Any non-comment match → `OwnershipCategory='SHARED'`,
  `Remarks='SHARED: referenced in <DataProviderFile>:<line>'`
- No qualifying match → `OwnershipCategory` unchanged (stays `LEGACY`),
  `Remarks='Reviewed for Blazor - no active DataProvider reference found (<date>)'`
  (clears the "pending" state so it isn't reprocessed, without falsely
  reclassifying it).

### Common

`ModifiedBy='Phase2Script'`, `ModifiedDate=GETDATE()` on every updated row.

## Execution

New script `update_ownership_category.ps1` (project root), PowerShell (per
existing skill notes, PowerShell-spawned sqlcmd against the network share/SQL
server works reliably where plain `cmd /c` via Bash can be flaky):

1. Read `db_config.txt` for connection details.
2. `sqlcmd` query: pending DATABASE rows (`ObjectType`, `ObjectName`, `FormId`,
   joined `FormName`) → temp file.
3. For each row, run the grep rule above against `D:\XpedeonSaas`, compute new
   `OwnershipCategory`/`Remarks`.
4. Build one `UPDATE MIG.OBJECT_OWNERSHIP SET OwnershipCategory=..., Remarks=...,
   ModifiedBy='Phase2Script', ModifiedDate=GETDATE() WHERE ObjectName='...' AND
   ObjectType='...'` per row, run as a single sqlcmd batch in a transaction.
5. Append one line per row to a new `ownership_update_log.txt`:
   `<timestamp> | <FormName> | <ObjectType> | <ObjectName> | <OldCategory> -> <NewCategory> | <Remarks>`

## Idempotency

Because every processed row's `Remarks` changes away from `'Category update
pending'`, re-running the script only picks up new pending rows created by
future phase-1 runs. Safe to re-run.

## Failure handling

If the sqlcmd update batch fails, log the sqlcmd error text to
`ownership_update_log.txt` and continue — same per-run tolerance as phase 1
(no partial-failure rollback across rows beyond the single transaction).

## Companion skill

Add `xpedeon-ownership-category-sync` skill (mirrors
`xpedeon-migration-form-sync`'s terse/verbose output pattern) so this becomes a
repeatable phase-2 step after each phase-1 run.

## Out of scope / not addressed

- BLL/DAL/UI rows.
- Rows already processed (Remarks no longer 'Category update pending').
- `SHARED` classification for TABLE rows (always `BLAZOR_OWNED` per decision).
