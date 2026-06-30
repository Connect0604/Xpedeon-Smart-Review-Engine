# Object Ownership Reclassification — Design

## Purpose

`list_folders.bat` inserts new forms into `MIG.FORM` and seeds their DB/code
objects into `MIG.OBJECT_OWNERSHIP` with `OwnershipCategory='LEGACY'`
(database layer) or `'RETIRING'` (code layers). This new tool re-evaluates
the `LEGACY` (Layer=`DATABASE`) rows against the actual Blazor codebase
(`D:\XpedeonSaas`) and reclassifies each object as `BLAZOR_OWNED`, `SHARED`,
or `RETIRED` based on whether it is still referenced there.

This is a standalone, separately-triggered tool (`update_ownership.bat` /
`update_ownership.ps1`), decoupled from `list_folders.bat`'s discovery flow.

## Candidate selection

Only rows matching exactly:

```sql
SELECT Layer, ObjectName, ObjectType, FormId
FROM MIG.OBJECT_OWNERSHIP
WHERE OwnershipCategory='LEGACY' AND Layer='DATABASE' AND Remarks='Category update pending'
  AND FormId IN (SELECT FormId FROM MIG.FORM WHERE ownership_updated='N')
```

`MIG.FORM.ownership_updated` (Y/N) scopes which *forms* are still pending.
Within a pending form, only `LEGACY` rows with the original
`'Category update pending'` Remarks are processed (rows already updated by
a prior partial run, e.g. now `BLAZOR_OWNED`, are not re-touched in this
pass — see Partial Failure below for the retry case).

## Usage scan

Scan `D:\XpedeonSaas` recursively, restricted to two path shapes:

- Tables: `*\SharedModels\Pocos\*.cs` (e.g.
  `Xpedeon.GlobalTaxManagement.SharedModels\Pocos\TanMaster.cs`)
- Procs: `*\Grpc\Services\DataProvider\*.cs` (e.g.
  `Xpedeon.GlobalTaxManagement.Grpc\Services\DataProvider\TanMasterDataProvider.cs`)

File contents are cached in memory once per run (not re-read per object).

Match rule, per object, across ALL repos (not scoped to the object's own
form/repo — cross-form reuse counts):

- `ObjectType='TABLE'`: regex `\[Table\("OBJECTNAME"\)\]`, case-insensitive,
  inside `Pocos\*.cs` files.
- `ObjectType='PROC'`: whole-word regex `\bOBJECTNAME\b`, case-insensitive,
  inside `DataProvider\*.cs` files.

Count = total regex match occurrences across all matching files (multiple
hits in the same file count individually).

## Classification

| Match count | New OwnershipCategory |
|---|---|
| 0 | `RETIRED` |
| 1 | `BLAZOR_OWNED` |
| 2+ | `SHARED` |

## Writeback

Per object, transactional via `sqlcmd`, same style as `list_folders.bat`:

```sql
BEGIN TRAN;
UPDATE MIG.OBJECT_OWNERSHIP
SET OwnershipCategory='<NEW>', Remarks='Ownership updated <date>',
    ModifiedBy='BatchScript', ModifiedDate=GETDATE()
WHERE ObjectName='<ESC>' AND ObjectType='<TYPE>' AND FormId=<FormId>;
COMMIT TRAN;
```

On `sqlcmd` failure for an object: do not modify that row (Remarks stays
`'Category update pending'`, so it retries next run); log to
`ownership_update_log.txt`.

## Form-level flag

After all candidate rows for a `FormId` are processed in this run:

- If **all** succeeded → `UPDATE MIG.FORM SET ownership_updated='Y' WHERE FormId=<FormId>`.
- If **any** object failed → leave `ownership_updated='N'` (form retried next
  run) and additionally:
  ```sql
  UPDATE MIG.FORM SET Remarks='Error:***<failure detail>' WHERE FormId=<FormId>
  ```

## Logging

New file `ownership_update_log.txt` (append-only, next to the scripts),
same style as `insert_log.txt` / `ownership_log.txt`:

```
====== Run started <date> <time> ======
<date> <time> | <ObjectName> | <OldCategory>-><NewCategory> | hits=<N> | OK
<date> <time> | <ObjectName> | FAILED | <sqlcmd error detail>
====== Run finished <date> <time> ======
```

## Script structure

- `update_ownership.ps1` — does the SQL query, the recursive scan, the
  classification, and the `sqlcmd` writeback calls.
- `update_ownership.bat` — thin wrapper: loads `db_config.txt` (same as
  `list_folders.bat`), invokes `powershell -File update_ownership.ps1`,
  pauses at the end unless an `auto` argument is passed (mirrors
  `list_folders.bat`'s `SKIPPAUSE` behavior).

## Out of scope

- Roslyn/AST-based parsing — not needed; both match targets are simple
  literal strings (`[Table("X")]` attribute argument, proc name as a plain
  string literal), so whole-word/regex text search is sufficient and far
  cheaper than building syntax trees across 58 repos.
- Code layers (`BLL`/`DAL`/`UI`, currently seeded as `RETIRING`) — out of
  scope for this pass; only `Layer='DATABASE'` rows are touched.
