# User Story Validation (TIER 1 - Feature #1)

## What Was Implemented

Added 8 migration-specific validation rules (MIGR-US-001 to MIGR-US-008) to ensure markdown documentation includes all critical fields for migration planning.

### Rules Summary

| Rule ID | Severity | Checks For | Why It Matters |
|---------|----------|-----------|----------------|
| MIGR-US-001 | ERROR | Description section | Orchestrator must understand what feature does |
| MIGR-US-002 | ERROR | Phase assignment | Phasing determines execution order |
| MIGR-US-003 | ERROR | Complexity assessment | Team capacity & risk planning |
| MIGR-US-004 | ERROR | Current tech docs | Understanding existing implementation |
| MIGR-US-005 | ERROR | Target tech docs | Architecture decisions for migration |
| MIGR-US-006 | WARNING | Acceptance criteria | Teams need explicit completion criteria |
| MIGR-US-007 | WARNING | Edge cases | Prevents surprises during development |
| MIGR-US-008 | INFO | Risk assessment | Mitigation planning & contingencies |

---

## Example: User Story That PASSES All Validation

```markdown
## Feature Group: Invoice Management

### US-001: Create Invoice with Line Items
**Status:** In Scope, Phase 1

### Description
Users can create a new invoice document with multiple line items, each specifying quantity, unit price, and description. The system validates all inputs and saves the invoice to the database.

### Phase
Phase 1 (Immediate) - Core feature needed for MVP cutover

### Complexity
Medium - Requires 3-5 days, standard CRUD pattern, straightforward data mapping

### Current Tech
**Platform:** WinForms with DevExpress GridControl
**Components:**
- InvoiceForm (WinForms)
- LineItemGrid (DxGrid)
- Validation: SQL Server stored procedure (sp_ValidateInvoice)
- Data Access: Direct SQL queries via SqlDataReader

### Target Tech
**Platform:** Blazor WebAssembly
**Components:**
- InvoiceCreatePage (Blazor component)
- LineItemDataGrid (DxDataGrid for Blazor)
- Validation: gRPC service (InvoiceService.ValidateInvoice)
- Data Access: EF Core with InvoiceEntity, LineItemEntity

### Acceptance Criteria
- [ ] User can enter invoice header (date, customer, reference)
- [ ] User can add/edit/delete line items in grid
- [ ] Validation matches legacy behavior (no negative amounts, required fields)
- [ ] Save creates database record matching legacy schema
- [ ] Form handles concurrent edits without data loss
- [ ] UI performance acceptable (<2s for save with 100 items)

### Edge Cases
- What if user saves invoice with no line items? (Reject or allow?)
- What if two users edit same invoice simultaneously? (Optimistic locking)
- What if invoice exceeds 1000 line items? (Performance limit?)
- What about null/empty customer reference? (Required or optional?)
- Negative unit price or quantity? (Always reject or allow adjustments?)

### Risks
- DevExpress GridControl API differs significantly from WinForms to Blazor
  - Mitigation: Prototype grid functionality in Phase 0 (pre-sprint)
- gRPC latency on line item add (no longer instant like WinForms)
  - Mitigation: Add optimistic UI update, validate on blur
- Data migration during parallel run (old & new systems both creating invoices)
  - Mitigation: Assign invoices created in Phase 2 only to new system
```

**Result:** ✅ PASSES - All required sections present, detailed, with concrete examples

---

## Example: User Story That FAILS Validation

```markdown
## Feature Group: Invoice Management

### US-002: Invoice Approval
Users can approve invoices through a workflow process.

### Phase
Phase 1

### Description
Approvers review and approve pending invoices.
```

**Failures:**
- ❌ MIGR-US-003: Missing `### Complexity`
- ❌ MIGR-US-004: Missing `### Current Tech`
- ❌ MIGR-US-005: Missing `### Target Tech`
- ❌ MIGR-US-006: Missing `### Acceptance Criteria`
- ❌ MIGR-US-007: Missing `### Edge Cases`
- ❌ MIGR-US-008: Missing `### Risks`

**Fix required before orchestrator can plan this story.**

---

## How to Format User Stories for Validation

### Required Structure

```markdown
## Feature Group: [Group Name]

### [US-XXX]: [User Story Title]

### Description
[Clear explanation of what users do and what system does]

### Phase
Phase 1 (or Phase 2, etc) - [brief justification]

### Complexity
[Easy/Medium/Hard] - [1-2 sentence explanation with effort estimate]

### Current Tech
**Platform:** [WinForms/Web/etc]
**Components:** [List relevant components, frameworks, patterns]
**Data Access:** [How is data currently accessed?]

### Target Tech
**Platform:** [Blazor/etc]
**Components:** [Target framework and components]
**Data Access:** [How will data be accessed in new system?]

### Acceptance Criteria
- [ ] [Testable criterion 1]
- [ ] [Testable criterion 2]
- [ ] [Testable criterion 3]

### Edge Cases
- [Edge case 1] → [How handled?]
- [Edge case 2] → [How handled?]
- [Edge case 3] → [How handled?]

### Risks
- [Risk description] → Mitigation: [mitigation approach]
- [Risk description] → Mitigation: [mitigation approach]
```

---

## How Validation Rules Work

Each rule searches for user story blocks matching `### US-XXX` pattern, then:

1. Finds the user story heading
2. Locates the next user story (or end of file)
3. Extracts the block between them
4. Checks if required section exists (case-insensitive regex)
5. Reports if section is missing

### Example Detection Logic (MIGR-US-001)

```
Input: "### US-001: Create Invoice\n### Phase\nPhase 1"
Regex: /### description/i (case-insensitive)
Result: ❌ NOT FOUND → Violation: "User story missing Description field"
Fix: "Add ### Description section with clear explanation"
```

---

## Next Steps (TIER 2)

After User Story Validation is working:

1. **Cross-MD Reference Validator** (MIGR-REF-001)
   - Detect components mentioned in US but not defined elsewhere
   - Flag inconsistent terminology across 5 MDs

2. **Phase Consistency Checker** (MIGR-PHASE-001)
   - Validate Phase 2 features don't depend on Phase 3 features
   - Flag unrealistic phase scope

3. **Technology Mapping Validator** (MIGR-TECH-001)
   - Ensure all current tech has corresponding target tech
   - Verify DevExpress components have Blazor equivalents

---

## Testing This Feature

### Test Case 1: Complete User Story
```
Upload MD with fully documented user story
Expected: ZERO validation errors
```

### Test Case 2: Missing Multiple Sections
```
Upload MD with user story missing Phase, Complexity, Target Tech
Expected: 3 errors reported (MIGR-US-002, MIGR-US-003, MIGR-US-005)
```

### Test Case 3: Multiple User Stories
```
Upload MD with 5 user stories, each missing different sections
Expected: All missing sections flagged across all stories
```

### Test Case 4: Non-US Content
```
Upload MD with regular content (not US-001, US-002 pattern)
Expected: No MIGR-US violations (rules only apply to user stories)
```

---

## Integration with Migration Workflow

```
1. Create Migration Charter
2. Map User Stories (20-30 critical journeys)
3. Generate 5 MDs organized by User Story
4. Smart Review Engine validates with MIGR-US rules
   └─ If errors: Fix MDs, re-upload
   └─ If clean: Ready for orchestrator
5. Claude Orchestrator reads validated MDs
   → Generates phased migration plan
6. Team approves plan
7. Development executes (with other TIER 1-4 validations)
```

---

## Success Criteria for Feature #1

✅ All user stories have complete documentation
✅ All required fields present and detailed
✅ Validation rules catch missing sections on upload
✅ Developers understand what makes a "complete" story
✅ Claude orchestrator receives high-quality input
