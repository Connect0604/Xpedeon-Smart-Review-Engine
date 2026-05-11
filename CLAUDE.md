# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Xpedeon Smart Review Engine** is a Blazor WebAssembly application that analyzes markdown files for quality issues based on a rulebook of antipatterns. Users upload `.md`, `.txt`, or `.markdown` files, which are parsed into sections and checked against predefined rules. The application displays violations organized by severity (error, warning, info) and provides actionable fix suggestions for each issue.

### Architecture

The project is a single-page Blazor WebAssembly application (.NET 9.0) with a two-panel UI:

**Left Panel**: Section navigator
- Lists all detected sections from the uploaded file (identified by headings)
- Shows section status (clean or has issues)
- Supports filtering (All, Issues, Clean) and text search
- Displays issue count by severity

**Main Panel**: Content reviewer
- Displays selected section content (rendered markdown)
- Shows violations ("Fix checklist") with:
  - Rule ID and severity level (error/warning/info)
  - Recommended fix text
  - Evidence showing matched text and reason
- Provides "Generate Revision Prompt" to create improvement suggestions based on all violations

### Core Concepts

**SectionModel**: Represents a markdown section identified by heading markers (e.g., `## Heading`). Contains the heading text, content, line numbers, and a unique ID.

**Violation**: A detected issue in a section. Contains:
- `RuleId`: Identifier for the broken rule
- `Severity`: error, warning, or info
- `Title`: Short description of the violation
- `Fix`: Recommended corrective action
- `Reason`: Explanation for why this is flagged
- `Matched`: The specific text that triggered the rule

**AntipatternRule**: A single quality rule defined in the rulebook. Rules are regex-based and are applied to all sections.

**Rulebook**: Created via `CreateRulebook()` static method. Contains all rules that violations are checked against. Rules are evaluated in parallel across all sections.

## Development Commands

### Running the application
```bash
dotnet run
```
Launches the development server at `http://localhost:5265` (HTTP) or `https://localhost:7009` (HTTPS). Browser opens automatically.

### Building the project
```bash
dotnet build
```
Compiles the project to `bin/Debug/net9.0/`.

### Cleaning build artifacts
```bash
dotnet clean
```
Removes all build output directories.

### Testing (if test project exists)
```bash
dotnet test
```

## Key Files

- **Pages/Home.razor**: Main application component. Contains:
  - File upload handling (drag-and-drop and file input)
  - Markdown parsing logic (`ParseSections()`)
  - Rule execution (`RunAllRules()`)
  - UI rendering and state management
  - Comprehensive styling (CSS Grid layout for two-panel design)

- **Program.cs**: Application entry point. Sets up the Blazor WebAssembly host and services.

- **App.razor**: Root component with router configuration.

- **Layout/MainLayout.razor**: Main layout wrapper.

- **SmartReviewSystem.csproj**: Project configuration (ASP.NET Core 9.0, Blazor WebAssembly).

- **_Imports.razor**: Global using statements for Razor components.

## Important Implementation Details

### File Upload
- Maximum file size: 10 MB
- Accepted extensions: `.md`, `.txt`, `.markdown`
- Handled via `HandleFileSelected()` method in Home.razor

### Markdown Parsing
- Sections are identified by heading markers (`#`, `##`, `###`, etc.)
- Each section has a unique ID, letter badge (A, B, C...), and line tracking
- Content is preserved as raw markdown for rendering

### Rule Application
- Rules are applied in parallel to all sections
- Violations are stored in `ViolationsBySection` dictionary (keyed by section ID)
- Rules check content using regex patterns and custom logic

### Markdown Rendering
- `RenderMarkdown()` converts markdown to HTML using basic parsing rules
- Supports headings, paragraphs, lists, blockquotes, code blocks, tables, and inline formatting

### UI State Management
- `SelectedSectionId`: Current section being reviewed
- `ExpandedReasonKeys`: Tracks which violation evidences are shown
- `SectionSearchText` and `SectionFilter`: Control section list visibility
- `ShowRevisionModal`: Controls visibility of the revision prompt modal

### Revision Prompt Generation
- `GenerateRevisionPrompt()` creates a consolidated text of all violations
- User can copy the prompt to clipboard for use with external tools
- Organized by section with violation details

## Styling Notes

All styling uses CSS variables (e.g., `--srs-text-primary`, `--srs-bg-primary`) for theming. The design uses a clean, accessible color palette with:
- Severity indicators: red (error), amber (warning), blue (info)
- Responsive layout with media queries for smaller screens
- Grid-based layout for the two-panel design

## Development Workflow

1. Modify `Home.razor` for UI changes or new features
2. Update the rulebook in `CreateRulebook()` to add/modify validation rules
3. Run `dotnet run` to test changes immediately
4. Browser DevTools (F12) can inspect the rendered component and styles
