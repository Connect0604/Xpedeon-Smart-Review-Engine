# Module Migration Validation (TIER 2 - Feature #2)

## Overview

Module Migration Validation ensures that each module's migration specification documents are complete and consistent before the Claude orchestrator processes them. A module migration specification consists of 5 markdown documents that collectively define how to migrate one business capability from WinForms to Blazor.

### The 5 Required Specification Documents

1. **Migration Charter**: Overview, scope, constraints, success criteria
2. **Legacy System Inventory**: Current implementation (stored procedures, forms, validation rules)
3. **Regression Test Catalog**: 60-100 test cases covering all behaviors
4. **State Transition Matrix**: Form lifecycle, field enabling/disabling logic
5. **Field-to-Domain Map**: Legacy columns → modern entities, API validation ownership

---

## What Was Implemented

Added 15 validation rules (MIGR-MOD-001 to MIGR-MOD-015) to ensure module migration specifications include all critical information for generating accurate implementation plans.

### Rules Summary

#### Specification Completeness (Rules 1-11)

| Rule ID | Document | Severity | Checks For | Why It Matters |
|---------|----------|----------|-----------|----------------|
| MIGR-MOD-001 | Charter | ERROR | Module scope definition | Defines what's included/excluded |
| MIGR-MOD-002 | Charter | ERROR | Success criteria | Objective completion measures |
| MIGR-MOD-003 | Inventory | ERROR | Current tech stack list | Must understand legacy platform |
| MIGR-MOD-004 | Inventory | ERROR | Stored procedure inventory | Must preserve all legacy logic |
| MIGR-MOD-005 | Inventory | ERROR | Business rule consolidation | Validation ownership & enforcement |
| MIGR-MOD-006 | Tests | ERROR | Test case scenarios | Behavioral contract & regression prevention |
| MIGR-MOD-007 | Tests | ERROR | Test classifications | UI/Domain/API/DB/E2E coverage |
| MIGR-MOD-008 | State Matrix | ERROR | Form lifecycle states | Entry point → completion path |
| MIGR-MOD-009 | State Matrix | ERROR | Field dependency matrix | Conditional enabling rules |
| MIGR-MOD-010 | Field Map | ERROR | Legacy-to-modern mapping | Table/column → entity/property |
| MIGR-MOD-011 | Field Map | ERROR | Validation ownership matrix | Layer enforcement (UI/API/DB) |

#### Implementation Readiness (Rules 12-15)

| Rule ID | Severity | Checks For | Why It Matters |
|---------|----------|-----------|----------------|
| MIGR-MOD-012 | WARNING | Test traceability markers | Track which test validates which code |
| MIGR-MOD-013 | WARNING | Deployment strategy outline | Parallel run, cutover, rollback plan |
| MIGR-MOD-014 | WARNING | Database schema compatibility | EF entity mapping 1:1 to DB tables |
| MIGR-MOD-015 | INFO | Performance targets | Load/save SLAs, grid size limits |

---

## Rule Details

### MIGR-MOD-001: Module Scope Definition

**What it checks:**
- Document contains a clear "## Scope" or "### Scope" section
- Scope lists what IS included in the migration
- Scope lists what is EXCLUDED/deferred

**Why it matters:**
- Orchestrator needs to know module boundaries
- Prevents scope creep (features sneaking in late)
- Defines what counts as "complete"

**Example of PASSES:**
```markdown
## Scope

### Included (Phase 1)
- Invoice creation with line items
- Basic validation (required fields, amounts)
- Save to database
- Edit existing invoices

### Excluded/Deferred (Phase 2)
- Invoice approval workflow
- Tax calculations
- Multi-currency support
- Duplicate invoice detection
```

**Example of FAILS:**
```markdown
## Implementation Plan

We will migrate the invoice system including all related features...
```
(No explicit scope section, unclear boundaries)

---

### MIGR-MOD-002: Success Criteria Definition

**What it checks:**
- Document contains a clear "## Success Criteria" or "### Success Criteria" section
- Criteria are testable/measurable (not subjective)
- Criteria cover functional, performance, and rollback aspects

**Why it matters:**
- Objective completion criteria prevent "done creeping"
- Orchestrator needs to know what "success" looks like
- Enables go/no-go cutover decisions

**Example of PASSES:**
```markdown
## Success Criteria

### Functional
- [ ] All 75 regression tests pass
- [ ] New Blazor app handles legacy schema 1:1
- [ ] All validation rules produce same error codes

### Performance
- [ ] Grid loads 500 items in <1.5s
- [ ] Save with 100 line items completes <2s
- [ ] gRPC latency <200ms at p95

### Cutover
- [ ] Parallel run stable for 2 weeks
- [ ] Zero data integrity issues
- [ ] Rollback to legacy completes <30min
```

**Example of FAILS:**
```markdown
### Completion Goals

We will build a high-quality solution that works well with the team.
```
(Subjective, unmeasurable)

---

### MIGR-MOD-003: Current Tech Stack

**What it checks:**
- Document lists the legacy platform (WinForms, WPF, etc.)
- Lists key frameworks/libraries (DevExpress version, GridControl, etc.)
- Lists data access pattern (direct SQL, ORM, stored procedures)

**Why it matters:**
- Orchestrator must understand current architecture
- Critical for knowing which legacy patterns to replicate
- Identifies dependencies to migrate or replace

**Example of PASSES:**
```markdown
## Current Tech Stack

**Platform:** WinForms (.NET Framework 4.7)
**UI Components:** DevExpress GridControl 22.1, DxGrid, DxComboBox
**Data Access:** Direct ADO.NET with SqlConnection, SqlDataReader
**Database:** SQL Server 2019 with stored procedures
**Key Libraries:** DevExpress, NLog
```

**Example of FAILS:**
```markdown
## Current Implementation

The invoice system is built with the legacy stack.
```
(Vague, no specific versions/frameworks)

---

### MIGR-MOD-004: Stored Procedure Inventory

**What it checks:**
- Document lists all stored procedures used by the module
- For each SP: purpose, parameters, result sets
- Identifies SPs that compute business logic (consolidation candidates)

**Why it matters:**
- SPs contain business rules that must transfer to new system
- Orchestrator needs to know if rules move to gRPC service, EF validation, or UI
- Prevents loss of critical validation logic

**Example of PASSES:**
```markdown
## Stored Procedure Inventory

### sp_GetInvoiceData
**Purpose:** Load all invoices for a customer
**Parameters:** @CustomerId (int), @DateFrom (datetime), @DateTo (datetime)
**Result Set:** Invoice ID, Date, Total, Status
**Business Logic:** None (pure data retrieval)
**Migration Plan:** Map to gRPC GetInvoices() → EF query

### sp_ValidateInvoiceLineItem
**Purpose:** Validate line item before insert
**Parameters:** @InvoiceId (int), @Quantity (decimal), @UnitPrice (decimal), @AccountCode (char)
**Validations:** Non-negative amount, valid account code, quantity > 0
**Business Logic:** Cross-references GL_ACCOUNTS for valid codes
**Migration Plan:** Split to API validation + EF validation
```

**Example of FAILS:**
```markdown
## Data Access

We use some stored procedures for data operations.
```
(No inventory, no procedures listed)

---

### MIGR-MOD-005: Business Rule Consolidation

**What it checks:**
- Document identifies all validation rules/constraints
- Rules are categorized by source (UI, SP, BLL, trigger, constraint)
- For each rule: current enforcement layer, modern ownership recommendation

**Why it matters:**
- Rules scattered across layers (UI, SP, BLL, triggers) are fragile
- Consolidation into domain layer improves reliability
- Orchestrator needs to know which layer owns validation

**Example of PASSES:**
```markdown
## Business Rule Consolidation (66 Rules)

### Validation Rules

| ID | Rule | Current Enforcement | Modern Owner | Notes |
|----|------|-------------------|--------------|-------|
| BR-INV-001 | Amount > 0 | UI TextBox.Validating | API DataProvider | Must reject in API layer |
| BR-INV-002 | Date >= Today | UI MaskEditProvider | UI + API | Display constraint + server validation |
| BR-INV-003 | Customer must exist | SQL FK constraint + UI | EF + API | EF enforces reference integrity |
| BR-INV-004 | Account code valid | sp_ValidateAccount | gRPC validation | Called before save |
| BR-INV-005 | Quantity not negative | GridControl ColumnEdit | API DataProvider | Check before line item save |
```

**Example of FAILS:**
```markdown
## Validation

The legacy system has various validation rules that we will implement in the new system.
```
(No detailed consolidation, no modern ownership)

---

### MIGR-MOD-006: Regression Test Scenarios

**What it checks:**
- Document includes 60+ test scenarios covering main user workflows
- For each test: scenario description, inputs, expected outputs
- Tests organized by area (happy path, edge cases, error handling)

**Why it matters:**
- Tests define the behavioral contract
- Orchestrator uses test descriptions to understand feature complexity
- Regression tests prevent accidental behavior breaks

**Example of PASSES:**
```markdown
## Regression Test Catalog (75 Tests)

### TC-001: Create Invoice with Single Line Item
**Scenario:** User enters new invoice header, adds 1 line item, saves
**Inputs:**
  - Date: 2024-01-15
  - Customer: ACC-123
  - Line Item: Qty=10, Unit Price=$5.00, Account=4100
**Expected Output:**
  - Invoice created in DB with ID
  - Line item attached with amounts calculated
  - Validation messages: none
**Test Level:** End-to-End

### TC-002: Reject Invoice with Negative Amount
**Scenario:** User attempts to save invoice with negative line item amount
**Inputs:**
  - Amount: -50.00
**Expected Output:**
  - Error message: "Amount must be positive"
  - Invoice NOT saved
  - Form remains in edit state
**Test Level:** API Integration, UI
```

**Example of FAILS:**
```markdown
## Test Cases

We have test cases for the invoice system functionality.
```
(No detailed test scenarios, no coverage breakdown)

---

### MIGR-MOD-007: Test Classification Levels

**What it checks:**
- Document classifies each test into levels: UI, Domain, API, DB, E2E
- Coverage matrix shows which test level covers which features
- At least 10% of tests at each critical level

**Why it matters:**
- Orchestrator uses test level distribution to assess code quality
- Ensures comprehensive coverage (not just UI-level tests)
- DB-level tests catch data integrity issues early

**Example of PASSES:**
```markdown
## Test Coverage by Level

| Level | Count | % of Total | Examples |
|-------|-------|-----------|----------|
| UI (Razor Component) | 15 | 20% | Form validation, grid binding, error display |
| Domain (Entity/DTO) | 12 | 16% | Enum mapping, validation rules, calculations |
| API (gRPC Service) | 25 | 33% | Data provider, validation, concurrent updates |
| DB Integration (EF) | 15 | 20% | Entity mapping, constraints, triggers |
| End-to-End | 8 | 11% | Full workflows, cutover scenarios |

**Total:** 75 tests, balanced across all levels
```

**Example of FAILS:**
```markdown
## Tests

All tests validate the invoice creation feature.
```
(No level classification, unknown coverage distribution)

---

### MIGR-MOD-008: Form Lifecycle States

**What it checks:**
- Document defines clear states (Load, Edit, Save, Close)
- Identifies transitions between states (entry conditions, exit actions)
- Documents what happens in each state (field visibility, enabling)

**Why it matters:**
- Form lifecycle drives UI behavior (enable/disable, show/hide)
- State machine prevents invalid transitions (e.g., save before load)
- Orchestrator uses this to generate PageModel event handlers

**Example of PASSES:**
```markdown
## Form Lifecycle State Machine

### States
- **Load**: Form initializes, pulls data from database
- **Edit**: User can modify fields, grid rows
- **Validate**: System checks all rules before save
- **Save**: Database transaction commits changes
- **Close**: Form unloads, state discarded

### Transitions
```
Load --(user clicks row)--> Edit
Edit --(user clicks Save)--> Validate --(all pass)--> Save --(user clicks Close)--> Close
Edit --(user clicks Save)--> Validate --(errors)--> Edit --(user fixes)--> Validate
Validate --(DB constraint fails)--> Edit
```

### State Actions
| State | On Enter | On Exit | Field Behavior |
|-------|----------|---------|----------------|
| Load | Query database | - | All disabled (read-only) |
| Edit | Enable form controls | Validate all | All enabled (except ID field) |
| Validate | Run business rules | - | All disabled during check |
| Save | Execute DB transaction | - | All disabled until Save complete |
| Close | Cleanup | - | Form hidden |
```

**Example of FAILS:**
```markdown
## User Interactions

Users can load, edit, and save invoices.
```
(No state definition, no transitions)

---

### MIGR-MOD-009: Field Dependency Matrix

**What it checks:**
- Document shows which fields enable/disable based on other field values
- Lists conditional logic (e.g., "InvoiceType=Credit shows RefundAccount")
- Identifies cascading dependencies

**Why it matters:**
- Complex enabling rules are easy to miss during migration
- Orchestrator uses this to generate PageModel logic
- UI correctness depends on accurate field visibility rules

**Example of PASSES:**
```markdown
## Field Enabling Rules (Conditional Logic)

| Trigger | Condition | Effect | Notes |
|---------|-----------|--------|-------|
| InvoiceStatus | = Draft | Enable: Edit, Delete | New invoices editable |
| InvoiceStatus | = Posted | Disable: Edit, Delete, Add Lines | Posted invoices locked |
| InvoiceStatus | = Voided | Disable: All fields | Voided invoices read-only |
| LineItemStatus | = Used | Disable: Edit, Delete | Mark prevents modification |
| AccountCode | Contains "RCVBL" | Show: CollectionDaysOverdue | Receivable-specific field |
| AllowPartialPayment | = True | Show: PartialAmountField | Credit memos allow partials |
| InvoiceType | = CreditMemo | Require: ReasonCode | Credit memos need reason |
| DocumentCurrency | != DefaultCurrency | Show: ExchangeRateField | Multi-currency rules |
| CustomerStatus | = Inactive | Show: WarningBanner | Flag inactive customers |
```

**Example of FAILS:**
```markdown
## Form Behavior

The form enables and disables fields based on user selections.
```
(No details on which conditions trigger which enabling rules)

---

### MIGR-MOD-010: Legacy-to-Modern Field Mapping

**What it checks:**
- Document maps every legacy database column to modern entity/property
- Shows data type conversions (if any)
- Identifies split/merge scenarios (legacy columns → new domain model)

**Why it matters:**
- 1:1 mapping ensures no data loss during migration
- EF entity configuration depends on correct mapping
- Orchestrator uses this to generate DTO/Proto contracts

**Example of PASSES:**
```markdown
## Field-to-Domain Mapping

| Legacy Table | Legacy Column | Data Type | Modern Entity | Modern Property | Conversion | Notes |
|--------------|---------------|-----------|---------------|-----------------|------------|-------|
| TBL_INVOICES | INV_ID | int | ContractPaymentHeader | Id | None | Primary key |
| TBL_INVOICES | INV_DATE | datetime2 | ContractPaymentHeader | InvoiceDate | None | Standard mapping |
| TBL_INVOICES | INV_AMOUNT | decimal(15,2) | ContractPaymentHeader | TotalAmount | None | No scale change |
| TBL_INVOICES | INV_CUST_CODE | char(10) | ContractPaymentHeader | CustomerId | Trim | Remove padding |
| TBL_INVOICES | INV_TYPE | char(1) | ContractPaymentHeader | InvoiceType | String→Enum | 'I'='Invoice', 'C'='Credit' |
| TBL_LINEITEMS | LI_ID | int | ContractPaymentLineItem | Id | None | Primary key |
| TBL_LINEITEMS | LI_ACCT_CODE | varchar(50) | ContractPaymentLineItem | AccountCode | None | FK to GL_ACCOUNTS |
| TBL_LINEITEMS | LI_ROW_VERSION | varbinary(8) | ContractPaymentLineItem | RowVersion | None | Concurrency token |
```

**Example of FAILS:**
```markdown
## Data Structure

Legacy invoices table has columns for invoice data that will be mapped to the new system.
```
(No detailed mapping, no conversions identified)

---

### MIGR-MOD-011: Validation Ownership Matrix

**What it checks:**
- Document specifies which validation layer owns each rule
- Rules distributed across UI, API gRPC service, EF constraints, DB
- No rule left without a modern owner

**Why it matters:**
- Clear ownership prevents validation inconsistencies
- Orchestrator uses this to generate correct code layer
- Prevents silent failures (rule enforced nowhere)

**Example of PASSES:**
```markdown
## Validation Ownership Matrix

| Rule ID | Rule | Legacy Owner | Modern Owner | Reason |
|---------|------|--------------|--------------|--------|
| VAL-001 | Amount > 0 | UI maskedit | API DataProvider | Server-side enforcement |
| VAL-002 | Date required | UI validator | API DataProvider + EF | DB constraint + gRPC validation |
| VAL-003 | Customer exists | SQL FK | EF + API | EF maps FK, API validates on insert |
| VAL-004 | Account code valid | sp_CheckAccount | gRPC GetAccounts + API filter | Called before save |
| VAL-005 | Quantity integer | UI maskedit | EF type definition | EF restricts to int32 |
| VAL-006 | No negative amounts | SQL check constraint | EF + DB check | Dual enforcement |
| VAL-007 | Date format MM/DD/YYYY | UI MaskEditProvider | UI Blazor input type | HTML5 date picker |
| VAL-008 | Description max 500 chars | SQL column size | EF MaxLength + DB constraint | Dual enforcement |
| VAL-009 | Invoice unique per customer/date | SQL unique constraint | API application logic | Cannot enforce in gRPC |
| VAL-010 | Status transition valid | BLL validation | Domain entity property | State machine logic |
```

**Example of FAILS:**
```markdown
## Validation

Rules are enforced in multiple places in the legacy system.
```
(No clear ownership mapping, undefined modern layer)

---

### MIGR-MOD-012: Test Traceability Markers (WARNING)

**What it checks:**
- Document includes test case IDs/names (e.g., TC-001, TC-InvoiceCreate)
- Implementation plan references which test validates each component
- Cross-references enable "change this code, know which tests broke"

**Why it matters:**
- Without traceability, developers don't know which tests to run
- Changes to code should trigger re-run of related tests
- Orchestrator can generate test command suggestions

**Example of PASSES:**
```markdown
## Test Traceability

### Code → Test Mappings

#### ContractPaymentHeaderService.GetData() (gRPC)
- Validated by: TC-001, TC-002, TC-003, TC-010
- Must not break: Load invoice, Filter by date, Handle concurrent queries

#### ContractPaymentLineItemGrid.EditModelSaving() (UI)
- Validated by: TC-015, TC-016, TC-017, TC-020, TC-025
- Must not break: Add line, Edit amount, Delete row, Validation error
```

**Example of FAILS:**
```markdown
## Tests

We have 75 regression tests covering the invoice functionality.
```
(Tests exist, but no traceability to code components)

---

### MIGR-MOD-013: Deployment Strategy (WARNING)

**What it checks:**
- Document outlines parallel run approach (old + new running together)
- Defines cutover steps (point-in-time switch-over)
- Describes rollback plan (if new system fails)
- Lists data migration approach (none needed per user clarification)

**Why it matters:**
- Risk management (parallel run validates before cutover)
- Cutover execution depends on clear steps
- Rollback plan determines RPO/RTO (Recovery Point/Time Objectives)

**Example of PASSES:**
```markdown
## Deployment Strategy

### Phase 1: Parallel Run (Weeks 1-2)
- Legacy system handles all invoice operations
- New Blazor app runs in read-only mode, consuming legacy data
- Manual validation: Reports generated by both systems match
- Success gate: Zero data discrepancies for 7 days

### Phase 2: Cutover (Day X)
1. Backup all databases (legacy + new)
2. Stop invoice entry in legacy system
3. One-time data sync: Pending invoices from legacy → new
4. Switch load balancer to new system
5. Monitor: Invoice operations proceed normally in new system
6. Duration: Target <30 minutes

### Phase 3: Rollback (If Needed)
1. Verify data consistency in new system
2. If issues: Restore from backup, revert load balancer to legacy
3. Duration: <15 minutes
4. Post-rollback: Re-sync data, retry cutover next day

### Data Migration
Not applicable. New system uses existing database schema 1:1. No transformation or cleanup needed.
```

**Example of FAILS:**
```markdown
## Implementation

We will build the new system and then deploy it to production.
```
(No parallel run, no cutover steps, no rollback plan)

---

### MIGR-MOD-014: Database Schema Compatibility (WARNING)

**What it checks:**
- Document confirms EF entities map 1:1 to existing SQL Server tables
- No schema migration/transformation required
- Confirms columns match EF property definitions

**Why it matters:**
- Prevents expensive data migrations
- Allows gradual cutover (old + new systems run simultaneously)
- Ensures data can flow seamlessly between old and new

**Example of PASSES:**
```markdown
## Database Schema Compatibility

### Zero-Change Approach

The new system uses the existing SQL Server database without modifications.

#### EF Entity Mappings
- ContractPaymentHeader → [dbo].[TBL_INVOICES] (1:1 mapping)
- ContractPaymentLineItem → [dbo].[TBL_LINEITEMS] (1:1 mapping)
- ContractPaymentAccountDefault → [dbo].[TBL_ACCOUNT_DEFAULTS] (1:1 mapping)

#### Column Compatibility
- All EF properties match existing database columns
- Data types preserved (no conversions needed)
- Constraints preserved (FK, checks, unique)
- Triggers preserved (TRG_FC_INS_*, TRG_FC_UPD_*, TRG_FC_DEL_*)

### Verification
- [ ] EF Core Entity Configurations match table definitions
- [ ] .HasTrigger() directives list all existing triggers
- [ ] Primary key columns match table structure
- [ ] Foreign key references verified
```

**Example of FAILS:**
```markdown
## Database

We will need to reorganize some tables to fit the new data model...
```
(Proposes schema changes, violates "zero-change" principle)

---

### MIGR-MOD-015: Performance Targets (INFO)

**What it checks:**
- Document specifies performance SLAs for key operations
- Targets include: page load time, grid rendering, save operation, gRPC call latency
- Targets are measured during parallel run (legacy vs. new comparison)

**Why it matters:**
- Prevents "performance cliff" where new system is much slower
- Orchestrator can generate performance test cases
- Provides go/no-go cutover decision criteria

**Example of PASSES:**
```markdown
## Performance Targets

### Grid Operations
- Load 500 invoice rows: < 1.5 seconds (legacy: ~1.2s)
- Render grid with 20 columns: < 500ms (legacy: ~300ms)
- Edit line item: Save completes < 2 seconds (legacy: ~1.8s)

### gRPC Operations
- GetInvoiceData() call: < 200ms p95 (includes DB + network)
- UpdateInvoiceData() call: < 800ms p95 (includes validation + transaction)

### UI Responsiveness
- Type in grid cell: Responsive within 100ms
- Scroll grid with 100 items: Smooth (>30fps)
- Modal dialog open/close: <300ms

### Cutover Success Gate
- User-observed latency must be ±10% of legacy (acceptable range)
- No customer complaints about slowness within first week
- CPU/memory usage on app server < 60% under normal load
```

**Example of FAILS:**
```markdown
## Performance

The new system should perform well.
```
(Vague, no measurable targets)

---

## Complete Validation Checklist

Use this checklist to verify your module migration specification is complete:

### Specification Completeness (Must All Pass)

- [ ] MIGR-MOD-001: Migration Charter includes Scope section
- [ ] MIGR-MOD-002: Charter includes Success Criteria (testable/measurable)
- [ ] MIGR-MOD-003: Charter lists Current Tech Stack (platform, frameworks, versions)
- [ ] MIGR-MOD-004: Inventory document lists all Stored Procedures with purpose/params/logic
- [ ] MIGR-MOD-005: Inventory includes Business Rule Consolidation (all 50+ rules listed)
- [ ] MIGR-MOD-006: Test Catalog includes 60+ regression test scenarios with inputs/outputs
- [ ] MIGR-MOD-007: Test document shows Level Classification (UI/Domain/API/DB/E2E coverage)
- [ ] MIGR-MOD-008: State Transition Matrix defines Form Lifecycle (Load/Edit/Validate/Save/Close)
- [ ] MIGR-MOD-009: State Matrix includes Field Dependency rules (conditional enabling)
- [ ] MIGR-MOD-010: Field-to-Domain Map shows legacy columns → modern properties (1:1)
- [ ] MIGR-MOD-011: Map includes Validation Ownership (which layer owns each rule)

### Implementation Readiness (Should Pass Before Development)

- [ ] MIGR-MOD-012: Test Catalog includes Test Traceability IDs (TC-001 format)
- [ ] MIGR-MOD-013: Deployment Strategy section describes parallel run, cutover, rollback
- [ ] MIGR-MOD-014: Schema Compatibility section confirms 1:1 EF mapping, no migrations
- [ ] MIGR-MOD-015: Performance Targets section specifies measurable SLAs

---

## Testing This Feature

### Test Case 1: Complete Module Specification
```
Upload 5 markdown files (Charter, Inventory, Tests, State, FieldMap)
All properly filled out with all 15 rules satisfied
Expected: ZERO validation errors (all green)
```

### Test Case 2: Missing Business Rule Consolidation
```
Upload module spec without MIGR-MOD-005 (no Business Rule Consolidation section)
Expected: One ERROR: "Module specification missing Business Rule Consolidation"
```

### Test Case 3: Incomplete Test Classification
```
Upload module spec where Test Catalog lists tests but no Level Classification matrix
Expected: One ERROR for MIGR-MOD-007: "Test Catalog missing Level Classification matrix"
```

### Test Case 4: Missing Performance Targets
```
Upload module spec without Performance Targets section
Expected: One INFO violation (MIGR-MOD-015), no ERROR
```

### Test Case 5: Vague Success Criteria
```
Upload module spec where Success Criteria are subjective ("works well", "is fast")
Expected: One WARNING: "Success Criteria must be testable/measurable"
```

---

## Integration with Migration Workflow

```
1. User Story Validation (TIER 1)
   └─ Ensures each user story has required structure

2. Module Migration Validation (TIER 2) ← YOU ARE HERE
   └─ Ensures each module migration spec is complete
   └─ 5 documents (Charter, Inventory, Tests, State, FieldMap) ready for orchestrator

3. Claude Orchestrator
   └─ Reads validated module specs
   └─ Generates 14-section Technical Implementation Plan
   └─ Defines EF entities, DTOs, gRPC services, UI components, test traceability

4. Implementation Quality (TIER 3)
   └─ Code must follow antipatterns (AP-UI-*, AP-BE-*, AP-XC-*)
   └─ Tests must validate traceability markers

5. Deployment & Verification (TIER 4)
   └─ Parallel run executes per deployment strategy
   └─ Performance targets verified before cutover
```

---

## Success Criteria for Feature #2

✅ All module migration specifications validated against 15 rules
✅ Specification completeness rules (1-11) catch missing documents/sections
✅ Implementation readiness rules (12-15) identify deployment risks early
✅ Developers understand what makes a "complete" module specification
✅ Claude orchestrator receives high-quality input (no guessing about architecture)
✅ Framework works for ANY module (not just invoice/contract payment examples)
