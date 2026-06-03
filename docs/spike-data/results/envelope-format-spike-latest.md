# Envelope Format Spike Results

- Samples: 15
- Formats: Json, Xml, Markdown
- Recommendation: Markdown
- Reason: Accuracy deltas stayed within 10 percentage points, so the plan defaults to Markdown-fenced for lower implementation cost.

## Aggregates

| Format | Samples | Overall Accuracy | Structure Integrity | Format Interference | Special Char Breakage |
|---|---:|---:|---:|---:|---:|
| Xml | 15 | 100.0% | 100.0% | 0.0% | 0.0% |
| Json | 15 | 98.3% | 100.0% | 0.0% | 0.0% |
| Markdown | 15 | 95.0% | 100.0% | 0.0% | 0.0% |

## Scenario Accuracy

| Scenario | Format | Accuracy |
|---|---|---:|
| failed_stacktrace | Json | 100.0% |
| failed_stacktrace | Markdown | 100.0% |
| failed_stacktrace | Xml | 100.0% |
| special_chars | Json | 100.0% |
| special_chars | Markdown | 100.0% |
| special_chars | Xml | 100.0% |
| success_diff | Json | 100.0% |
| success_diff | Markdown | 91.7% |
| success_diff | Xml | 100.0% |
| success_long_log | Json | 100.0% |
| success_long_log | Markdown | 100.0% |
| success_long_log | Xml | 100.0% |
| success_short | Json | 100.0% |
| success_short | Markdown | 100.0% |
| success_short | Xml | 100.0% |
| waiting_user | Json | 87.5% |
| waiting_user | Markdown | 75.0% |
| waiting_user | Xml | 100.0% |
