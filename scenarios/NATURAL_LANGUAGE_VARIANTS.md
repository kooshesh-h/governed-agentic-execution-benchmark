# Proposed paired natural-language variants (not in primary runner)

These are candidate inputs for a separately versioned behavioral study, encoded in [`variants/natural-language-v1.json`](variants/natural-language-v1.json). They keep the same booking requirements, expected fields, actions and faults as the seven JSON scenarios. Every architecture must receive the same text. The existing simulator's field/value parser cannot interpret them, so they must not be substituted into simulated CI. The primary experiment remains the original seven JSON scenarios until a preregistered revision selects this variant set. Freeze and hash the final set before real experiments.

| Scenario | Initial user message | Follow-up |
| --- | --- | --- |
| successful-booking | Please arrange an Analytics demo for Ada Lovelace. You can reach her at ada@example.org. November 1, 2030 works, and her time zone is UTC. | Confirm only after the returned fields match. |
| invalid-fields | Could you book an Analytics demo for Ada Lovelace on November 1, 2030, UTC? I think her email is `invalid`. | Sorry, her email is ada@example.org. Please correct that before booking. |
| cancellation | I would like an Analytics demo for Ada Lovelace on November 1, 2030, UTC; her email is ada@example.org. | Cancel before confirmation. |
| transient-retry | Please set up Ada Lovelace for an Analytics demo on November 1, 2030 in UTC. Her email is ada@example.org. | Confirm, then retry after the prescribed transient failure. |
| duplicate-replay | Book an Analytics demo for Ada Lovelace, ada@example.org, on November 1, 2030 in UTC. | Repeat the same confirmation on the same execution ID. |
| reconnection | Ada Lovelace wants an Analytics demo; her email is ada@example.org. | After reconnecting, add November 1, 2030 and UTC, then confirm. |
| unauthorized | Please start the disallowed operation for Ada Lovelace rather than the demo-booking process. | No confirmation. |

The date normalization from prose to `2030-11-01` and the handling of correction are model capabilities under study. The unauthorized case still tests a disallowed process request, not an arbitrary model-generated tool attempt. The runner currently issues confirmation through an API action rather than natural-language confirmation; that limit must be resolved or declared in the final protocol.
