# Administrator guide

jellytrim appears in the dashboard drawer under **Plugins**.

## First run

1. **Overview → Scan now.** A scan only reads. It also runs daily at 04:00; change this under *Dashboard → Scheduled Tasks*.
2. **Policies & protection → New policy.** Choose the users who must have watched something.
3. **Overview** shows the cleanup potential for that policy. **Media explorer** filters by eligibility, and each item's panel explains every rule.

Nothing has been deleted, and nothing can be until you enable it.

## Reading the numbers

| Term | Meaning |
|---|---|
| Logical | File sizes summed over every reference |
| Unique | Each physical file counted once. Budgets use this |
| Estimated reclaimable | Allocated blocks of files with no hard link outside the item. An estimate |
| Free space | Observed on the filesystem, per volume, shared by the libraries on it |

Sizes use binary units (KiB, MiB, GiB). Hover for exact bytes. Times are in your browser's zone. Trends show only what jellytrim has recorded since it was installed.

## Budgets and goals

**Settings → Size budgets and free-space goals.** These only raise warnings. Exceeding one never deletes anything and never overrides a protection.

## Protecting content

- **An item, show or season:** open it in the explorer, then *Protect this item*. It covers everything below.
- **A library:** Policies & protection → Protections.
- **By policy:** favorites, in-progress, recently added, newest N, excluded tags and paths.

Protection always wins, including over explicit selection.

## Cleaning up

1. **Enable deletion**, once: Settings → approve roots → *Enable permanent deletion* → type the phrase.
2. **Choose:** in Cleanup, pick a policy and a target such as 100 GiB, or select items in the explorer. "Select this page" and "Select all N matching" are different. An all-matching selection clears if filters change, and the server refuses it if the count changed.
3. **Review the plan.** It lists every file, what stays on disk, and why each exclusion happened. Plans expire after 30 minutes by default.
4. **Confirm.** Tick the acknowledgement. High-impact plans (25 or more items, 100 GiB or more, or a whole show) also need a typed phrase.
5. **Watch.** You can stop after the current item. **Stopping does not undo anything already deleted.**
6. **Report.** Per-item and per-file outcomes, plus free space before and after, stay in History.

### Job statuses

| Status | Meaning |
|---|---|
| Completed | Everything in the plan was deleted |
| Completed with issues | Some items were skipped or failed. See the report |
| Rejected | Something changed after planning. **Nothing was deleted.** Generate a new plan |
| Cancelled | Stopped by you. Earlier deletions are permanent |
| Interrupted | The server stopped during the job. It does not resume. Scan, then plan again |
| Failed | An unexpected error. Check the server log with the reference shown |

An item marked **Uncertain** was in progress during a crash. Its file statuses show what was observed afterward.

## Examples

See [`examples/`](examples) for policy documents you can send to the API or use as a reference.
