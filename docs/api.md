# API

All routes are under `/Jellytrim`, require a Jellyfin administrator, and take the token in the `Authorization: MediaBrowser Token="..."` header. These routes are deliberately left out of Jellyfin's OpenAPI document: its generator has one flat schema namespace, and a plugin type sharing a name with a server type breaks the whole document. This page is the API reference. Responses are camelCase JSON. Requests use the same names, matched case-insensitively.

Any request may send `X-Correlation-Id`, of up to 64 letters, digits and hyphens. Responses echo it.

## Errors

```json
{ "code": "PLAN_EXPIRED", "message": "This plan expired. Generate a new plan.", "correlationId": "…", "details": null }
```

| Code | Status | Meaning |
|---|---|---|
| `VALIDATION_FAILED` | 400 | Body or query invalid; `details` lists problems |
| `CONFIRMATION_REQUIRED` | 400 | Acknowledgement or exact phrase missing |
| `ROOT_NOT_LIBRARY_LOCATION` | 400 | An approved root is not a library location |
| `API_KEY_NOT_ALLOWED` | 403 | Requires a signed-in administrator |
| `NOT_FOUND` | 404 | |
| `REVISION_CONFLICT` | 409 | Someone else changed it; reload |
| `SCAN_RUNNING`, `CLEANUP_RUNNING`, `JOB_ALREADY_ACTIVE` | 409 | Busy |
| `NO_INVENTORY` | 409 | Run a scan first |
| `DELETION_DISABLED` | 409 | |
| `SELECTION_CHANGED` | 409 | Matching results differ from `expectedCount` |
| `PLAN_TOO_LARGE`, `PLAN_EMPTY`, `PLAN_HASH_MISMATCH`, `PLAN_PREVIEW_ONLY`, `PLAN_ALREADY_EXECUTED` | 409 | |
| `JOB_NOT_ACTIVE` | 409 | |
| `PLAN_EXPIRED` | 410 | |
| `INTERNAL_ERROR` | 500 | See the server log |
| `DATABASE_UNAVAILABLE` | 503 | See install guide |

## Routes

| Method and path | Purpose |
|---|---|
| `GET Status` | Version, mode, scan, active job |
| `GET Overview?userIds=&policyId=` | Sizes, volumes, budgets, largest, watch summary, potential, warnings |
| `GET Trends?days=` | Snapshots |
| `GET Users` | Users, for selectors |
| `POST Items/Query` | Explorer: filters, sort, `offset`, `limit` of at most 200 |
| `GET Items/{id}?policyId=` | Files, per-user state, protections, rule explanations |
| `GET, POST Policies`; `PUT, DELETE Policies/{id}`; `POST Policies/{id}/Evaluate` | `PUT` takes `{policy, expectedRevision}`. `PUT` and `DELETE` are refused for API keys |
| `POST Recommendations` | `{policyId, targetBytes}` |
| `GET, POST Protections`; `DELETE Protections/{targetId}` | `DELETE` is refused for API keys |
| `GET, PUT Settings` | `PUT` takes `{settings, expectedRevision, enableConfirmation}`. Refused for API keys |
| `POST Scans`; `GET Scans/Current` | Start and watch a scan |
| `POST Plans` | `{type: Items\|Query\|Policy, itemIds, query, expectedCount, policyId, targetBytes, includeSidecars}` |
| `GET Plans/{id}?itemOffset=&itemLimit=&exclusionOffset=&exclusionLimit=` | Paged plan |
| `POST Plans/{id}/Execute` | `{planHash, idempotencyKey, acknowledged, confirmationPhrase}`. Refused for API keys |
| `GET Jobs`; `GET Jobs/{id}?offset=&limit=&status=`; `POST Jobs/{id}/Cancel` | |
| `GET Audit?offset=&limit=` | |

Pages are bounded: `limit` is 1 to 200. Retrying `Execute` with the same `idempotencyKey` returns the same job.
