# ADR-0005: No scheduled deletion

**Decision:** scans run on a schedule; deletion only runs from a plan confirmed by a signed-in administrator. API keys cannot execute plans. Budgets only warn.

**Why:** the whole safety model rests on a person reviewing an exact list. Automation needs its own bounded approval design, and a partial version would be the most dangerous feature in the product. A test ensures that the only scheduled task is the scan.

**Revisit if:** the interactive path has proved itself on real libraries.
