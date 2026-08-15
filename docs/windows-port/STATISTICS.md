# Statistics data contract

The Statistics window presents provider activity reconstructed from successful local quota refreshes.
It does not treat quota history as token usage, cost, prompt count, completed work, or productivity.

## Quota points

- Samples are grouped by equivalent provider reset time.
- The first sample in a reset cycle is a baseline and contributes no activity.
- A later sample contributes only the positive increase above the highest previously observed value in that cycle.
- Provider corrections, temporary dips, and repeated values do not count the same quota increase twice.
- A quota point is one observed percentage-point increase in the selected provider series.

## Coverage and time

- Days and hours use the machine's local time.
- `No local observation` means no successful sample covered that period; it is distinct from an observed value of zero.
- Future dates cannot be opened.
- Weeks start on Sunday. Month totals include only days that fall inside that calendar month, while a selected week can cross a month boundary.
- The overview contains the latest 52 Sunday-based calendar weeks.

## Measures and scaling

- The current implementation exposes quota points only. A Tokens control must not appear until exact historical token data exists for the selected provider series.
- Calendar intensity is relative to the selected series' observed active-day distribution. Absolute scaling remains unavailable until product thresholds have a defensible meaning.
