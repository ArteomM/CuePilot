# Jira Import Guide (Sprint 0/1)

## Files
- `docs/planning/jira-import-epics.csv`
- `docs/planning/jira-import-issues-sprint0-1.csv`

## Import Sequence
1. Import epics from `jira-import-epics.csv`.
2. Capture generated epic keys in Jira (for example `CP-1`, `CP-2`, ...).
3. Open `jira-import-issues-sprint0-1.csv` and replace each `<replace-with-epic-key>` with the corresponding real epic key.
4. Import issues CSV.
5. Assign imported tasks to boards/sprints if your Jira setup does not auto-create sprint values.

## Field Mapping Recommendations
When Jira asks to map CSV fields, map:
- `Issue Type` -> Issue Type
- `Summary` -> Summary
- `Description` -> Description
- `Priority` -> Priority
- `Labels` -> Labels
- `Sprint` -> Sprint (if available in your project type)
- `Epic Name` -> Epic Name (epic rows only)
- `Epic Link` -> Epic Link (task rows)

## Notes
- `Dependencies` and `Acceptance Criteria` are provided as explicit text columns to keep implementation constraints visible.
- If your Jira workflow requires `Story` instead of `Task`, map `Issue Type` values during import.
- If `Sprint` cannot be imported directly, use a saved filter (`labels = cuepilot AND Sprint is EMPTY`) and bulk-assign after import.
