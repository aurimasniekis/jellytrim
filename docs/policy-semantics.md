# Policy semantics

Every rule below is pinned by a test in `tests/Jellytrim.Core.Tests/PolicyEvaluatorTests.cs`.

## How rules combine

An item is eligible only if **no rule returns Block or Unknown**. All rules always run, so every reason is reported.

- **Criteria** (AND): played requirement, last-played age, added age, minimum size, scope, whole-unit completeness.
- **Protections** override all criteria: explicit protection, favorites, in-progress, keep recently added, keep newest, excluded library/path/tag, active playback.
- **Availability:** the files must have been observed.

## Unknown fails closed

Evaluation is three-valued. `All` follows Kleene AND: a definite "no" decides, otherwise any unknown stays undecided. `Any` follows Kleene OR: a definite "yes" decides. Either way, Unknown blocks deletion.

## Definitions

| Question | Answer |
|---|---|
| **Effective users?** | Participating minus excluded. |
| **Empty set?** | Never eligible. A stored policy cannot be saved with one, and the evaluator blocks it regardless. |
| **Played** | Jellyfin's flag. It does not prove playback completed; users can set it manually and the API cannot distinguish the two. |
| **In progress** | Any saved resume position, *even if Played is also set*, since that is a rewatch. |
| **Same user for all predicates?** | No. Each rule is evaluated independently over the effective users. |
| **Last watched** | The most recent time across effective users, including in-progress users. It is one timestamp per user, not a history. |
| **Missing timestamp** | A played user with no time, or an unknown user, makes the rule Unknown, unless a known-recent play already decides Block. |
| **Age boundaries** | Elapsed UTC time, so independent of time zone. Exactly N days counts as "at least N days ago". |
| **Missing date added** | Unknown for both date rules. Jellyfin's `DateTime.MinValue` is read as missing. |
| **Deleted user** | Unknown on every item. Items then fail in-progress and favorite checks in the selected scope, and all-mode played checks. Remove them from the policy. |
| **Disabled user** | Still counted, since their data is still readable. Exclude them explicitly if they should not count. |
| **No library access** | Unknown, not Unplayed. Under All-selected this keeps the library's items. Parental ratings are not considered. |
| **State read failure** | Unknown for that user. |
| **Favorites** | Scope `All known users` (default), `Selected users`, or off. A favorite on a show or season protects its episodes. |
| **In-progress scope** | `All known users` (default) or `Selected users`. It cannot be turned off. |
| **Tags** | Inherited: a tag on the show, season or library applies to its episodes, matched case-insensitively. |
| **Protection propagation** | Library → show → season → episode. Protection is keyed to Jellyfin's item id and does not survive the item being re-created. |
| **Versions** | State is merged across all versions: any in progress wins, then any played. Favorites combine. Play counts sum. All versions are deleted together; deleting one version is not supported. |

## Episodes

**Keep newest N:** within a show, regular episodes sort by (season, episode) and the top N are protected. Specials (season 0) neither count nor are protected by this rule, but must pass all others. Any regular episode with no season or episode number makes the whole show Unknown. Virtual episodes and episodes whose file is missing take no slot, since they cannot be kept. Episodes on offline storage do take a slot, since they may exist.

**Complete season or show:** every file-backed episode must qualify individually. A missing or unavailable episode blocks the unit. Virtual episodes do not. With a reclaim target, a selected episode brings in the rest of its unit, so a target can be exceeded.

## Explicit selection

Selecting items by hand uses a fixed rule set: no played requirement, with favorites and in-progress protected for all users, plus every availability check and protection. Selecting a show or season expands to its episodes, each evaluated individually: **a show is never assumed safe because some episodes are watched.**

## Execution time

The plan embeds its policy revision. Execution re-reads user state, favorites, protections and playback live. Changing the policy invalidates its plans. `unit.complete` is not recomputed per item at execution, because sibling state is not re-read: each episode's own state is what gets re-verified.
