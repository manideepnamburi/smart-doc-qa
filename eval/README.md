# SmartDocQA Eval Harness

A RAG evaluation harness that runs a golden Q&A dataset against the live
SmartDocQA API and scores the results on retrieval hit-rate, answer
faithfulness, and citation accuracy — using one or more configurable
LLM judges.

## What it measures

1. **Retrieval hit-rate** (deterministic, no LLM) — for each question, does the
   expected source page appear among the API's returned citations? For
   "not_found" category questions, a hit means the system correctly returned
   `AnswerSource: NotFound` instead of hallucinating an answer.
2. **Faithfulness** (1-5, LLM-as-judge) — does the answer match the key facts
   in the reference answer, without fabrication?
3. **Citation accuracy** (1-5, LLM-as-judge) — do the cited pages plausibly
   support the answer?

## Configuring judges (`judges-config.json`)

Judges are listed in `judges-config.json`, each with a `name`, `provider`
(`anthropic` or `openai`), `model`, and `apiKeyEnvVar` — the name of the
environment variable holding that judge's API key. **The key itself never
goes in this file** — only the name of the env var to read it from, same
secrets discipline as the rest of this project.

```json
{
  "judges": [
    { "name": "haiku", "provider": "anthropic", "model": "claude-haiku-4-5-20251001",
      "apiKeyEnvVar": "ANTHROPIC_API_KEY", "enabled": true },
    { "name": "gpt4o-mini", "provider": "openai", "model": "gpt-4o-mini",
      "apiKeyEnvVar": "OPENAI_API_KEY", "enabled": false }
  ]
}
```

Set `"enabled": true` on as many judges as you want to run in a single pass —
every enabled judge scores every question, and the report shows per-judge
averages plus a list of questions where judges disagreed on pass/fail. This
is useful both for swapping judges over time (newer model, cheaper model) and
for sanity-checking that a single judge isn't systematically biased.

To add a new judge, add an entry here — no code changes needed unless it's a
provider other than Anthropic/OpenAI, in which case add a new `IJudgeCaller`
implementation in `JudgeCallers.cs`.

The harness gates its exit code (0/1, for CI/CD use) on the **first enabled
judge's** pass rate. With multiple judges active, decide later whether you
want the gate to require all judges to agree, or just one.

## Prerequisites

1. The SmartDocQA API running locally (`dotnet run --project src/SmartDocQA.API`)
   with the NHANES webinar PDF already ingested:
   ```bash
   curl -X POST https://localhost:7XXX/api/documents/ingest/local \
     -H "Content-Type: application/json" \
     -d '{"path": "C:/path/to/nhanes-webinar-05-05-2020.pdf"}'
   ```
2. API keys for every *enabled* judge, set as environment variables matching
   each judge's `apiKeyEnvVar`. The included `run-eval.local.ps1` pattern
   (gitignored) is the recommended way to set these — see below.

## Running

Recommended: create `eval/run-eval.local.ps1` (already gitignored) with:
```powershell
$env:SMARTDOCQA_API_BASE_URL = "https://localhost:59776"
$env:ANTHROPIC_API_KEY = "sk-ant-your-real-key"
# $env:OPENAI_API_KEY = "sk-your-openai-key"   # only if an OpenAI judge is enabled
dotnet run
```

Then just:
```bash
cd eval
.\run-eval.local.ps1
```

Optional positional arguments if calling `dotnet run` directly:
`dotnet run -- path/to/golden-dataset.json path/to/output-report.json path/to/judges-config.json`

## Output

- Console: live progress per question (per judge), then a per-judge summary
  block, then a cross-judge disagreement list if more than one judge ran.
- `eval-report.json`: full per-question results including every judge's score
  and reasoning, for inspection or trend-tracking across runs.

## Extending the golden dataset

Each entry in `golden-dataset.json` needs:
- `question` / `expectedAnswer` — written by hand from the actual source
  document, not generated
- `expectedPageNumber` — approximate; retrieval scoring allows ±1 page since
  chunk boundaries don't always land exactly on the right slide
- `category` — `"factual"`, `"numeric"`, or `"not_found"` (the last for
  negative test cases that verify the system doesn't hallucinate answers to
  out-of-scope questions)

When adding a new source document, keep the same discipline: every expected
answer must be traceable to a specific page, never invented or paraphrased
from memory of "what these documents usually say."
