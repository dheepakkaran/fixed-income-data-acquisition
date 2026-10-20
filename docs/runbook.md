# Runbook

Operational procedures. Every command assumes the repository root and a reachable database
(`docker compose up -d` locally).

---

## Rerun a failed ingestion

**1. Find out what failed and why.**

```bash
dotnet run --project src/DataAcquisition.Cli -- status --limit 20
```

The `status` column and the error message tell you which of three things happened.

```sql
-- Or straight from the database, with the full message:
select id, provider_name, subject_id, reporting_period, status, records_parsed,
       validation_errors, error_message
from ingestion_runs
where status = 2            -- 2 = Failed
order by id desc limit 10;
```

**2. Read the failure class, because the fix differs.**

| `error_message` begins | What happened | What to do |
|---|---|---|
| `fetch:` | The download failed after three retries with backoff. | Usually transient. Rerun. If it persists, check SEC or Treasury is up and that the User-Agent is being sent. |
| `validation failed:` | Real data broke a rule. Nothing was committed. | **Do not just rerun** — it will fail identically. Go to step 4. |
| `parsed 0 records` | The source produced nothing. | The file, the format or the parser is wrong. Go to step 5. |

**3. Rerun.** Successful sources are skipped by content hash, so a rerun only redoes what
failed and makes no network request for anything already cached.

```bash
dotnet run --project src/DataAcquisition.Cli -- ingest --periods 3
```

To narrow it to one trust:

```bash
dotnet run --project src/DataAcquisition.Cli -- ingest --subject 2099048 --periods 1
```

**4. For a validation failure**, the message carries per-rule counts and up to five example
messages. Decide whether the data is genuinely bad or the rule is wrong:

```
validation failed: 412 error(s) across 29,152 record(s)
  [29,152 records, value-range=412(E), required-field=0(E)]
```

Real filings are untidy in ways the schema permits — see
[RESULTS.md section 5](../RESULTS.md). If this is a shape the schema allows and the rule did
not anticipate, the rule is wrong: change the severity to `Warning` or narrow the rule, add a
test pinning the new case, and record it in [decisions.md](decisions.md). If the data really
is broken, the run stays failed and nothing publishes, which is the intended outcome.

**5. For an empty source**, check whether the exhibit itself is empty before suspecting the
parser:

```bash
grep -c '</assets>' .cache/SecAbsEe/<accession>-<file>.xml
```

If the file has records and the parser returned none, the likely cause is an `XmlReader`
advance bug — see the trap in [adding-a-provider.md](adding-a-provider.md#3-write-a-streaming-parser).
That exact failure produced "0 parsed, 0 loaded, no rule breaches" once.

**6. Force a re-ingest** when you need to reload a source that already succeeded — a corrected
file, or a schema change that means the stored rows are stale:

```bash
dotnet run --project src/DataAcquisition.Cli -- ingest --subject 2099048 --force
```

`--force` is safe. The load upserts on the natural key, so rows are updated in place rather
than duplicated; it writes a second succeeded run row, which is why the identity index is not
unique.

---

## Backfill a month

ABS-EE is filed monthly, a few weeks after each period ends. `--periods N` takes the **N most
recent** filings per trust, so a backfill is simply a larger N.

```bash
# The last six filings per trust instead of three.
dotnet run --project src/DataAcquisition.Cli -- ingest --periods 6
```

Check what arrived:

```sql
select trust_cik, reporting_period_end, count(*) as loans
from auto_loan_asset_data
group by 1, 2
order by 1, 2 desc;
```

**Before you start, work out the download.** Each exhibit is 120–160 MB and each trust files
one per month, so six periods across three trusts is roughly 2.5 GB. Already-cached periods
cost nothing: `ingest` reads the hash off disk and skips without a request.

```bash
du -sh .cache/SecAbsEe
```

**There is no way to request one specific historical period.** `--periods` counts back from
the newest. To reach an older one, raise N until it is included; everything newer is skipped
on the second pass anyway.

**Treasury backfills by year**, not by month — the feed is published a calendar year per
request:

```jsonc
// src/DataAcquisition.Cli/appsettings.json
"TreasuryYields": { "Years": 5 }
```

Mind the latency: about 19.5 seconds per year, because the endpoint generates the whole year
per request.

**After a backfill, regenerate the report.** The views read whatever is in the tables, but the
published page does not change until it is rebuilt:

```bash
dotnet run --project src/DataAcquisition.Cli -- report --out ./site
```

---

## What to check when the perf gate fails

The gate compares mean time and allocated memory per benchmark with
`benchmarks/baseline.json` and fails past **15%**. It is enforcing on pull requests into `main`
and report-only into `develop`.

**1. Read the table in the job summary or the PR comment.** Regressed benchmarks are marked
⚠️ with both the time and the memory change.

**2. Work through these in order, because the first two are far more common than a real
regression.**

| Check | How to tell | What to do |
|---|---|---|
| **Is the baseline from the same class of machine?** | Look at `runtime` in `benchmarks/baseline.json`. A local capture compared against `ubuntu-latest` is measuring hardware. | Refresh it: run the **Capture perf baseline** workflow from `main`, download the artifact, commit it through a `chore/` branch into `develop`. |
| **Did everything regress by a similar amount?** | All benchmarks up together, including ones the change does not touch. | Almost certainly the machine or a noisy runner, not the change. Re-run once; if it persists, refresh the baseline. |
| **Is it only one or two benchmarks, and plausibly related to the diff?** | A parser change moving the parser benchmarks and nothing else. | A real regression. Go to step 3. |

A documented instance of the first row: a baseline captured on an Apple M5 and compared
against `ubuntu-latest` reported **all eight** benchmarks as regressed, including one the
change measured at **−5.1%** locally. See [decisions.md](decisions.md) D18.

**3. Reproduce it locally**, where a clean comparison is possible:

```bash
docker compose up -d
dotnet build -c Release -warnaserror
dotnet run --project benchmarks/DataAcquisition.Benchmarks -c Release --no-build -- --filter '*'
dotnet run --project tools/PerfGate -c Release --no-build -- \
  compare --results BenchmarkDotNet.Artifacts/results \
          --baseline benchmarks/baseline.json --threshold 15
```

Parser benchmarks alone need no database:

```bash
dotnet run --project benchmarks/DataAcquisition.Benchmarks -c Release -- --filter '*ParserBenchmarks*'
```

**4. Fix the regression** rather than the threshold. Raising the threshold to get green is how
a gate stops being one — see D17. If the slowdown is genuinely worth it, say so in
`decisions.md`, refresh the baseline from `main`, and explain the trade in the release PR.

**5. Expect three outcomes that are not failures.** A missing baseline, a new benchmark and a
baseline entry with no matching result are all reported rather than failed, because each is a
false alarm rather than a slow change.

---

## Add a trust

Entirely configuration. No code, no migration.

**1. Confirm the trust files ABS-EE, and that it is an auto *loan* trust.**

Auto **lease** trusts use a different ABS-EE schema and will not parse with this provider.
Names containing *Lease* or *Leasing* are the giveaway.

```bash
UA="Your Name your.email@example.com"
curl -sS -A "$UA" "https://data.sec.gov/submissions/CIK0002099048.json" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); r=d['filings']['recent']; \
      print(d['name']); print(d.get('sicDescription')); \
      print([x for x in zip(r['form'], r['reportDate']) if x[0]=='ABS-EE'][:3])"
```

Expect SIC 6189 *Asset-Backed Securities* and recent `ABS-EE` entries.

**2. Check the exhibit size.** The provider refuses anything over `MaxExhibitBytes`
(400 MB default), and the practical ceiling for comfortable local work is around 150 MB.

```bash
curl -sS -A "$UA" \
  "https://www.sec.gov/Archives/edgar/data/2099048/000209904826000028/index.json" \
  | python3 -c "import json,sys; [print(f\"{i['name']}  {int(i['size'])/1e6:.1f} MB\") \
      for i in json.load(sys.stdin)['directory']['item'] if i['name'].endswith('.xml')]"
```

**3. Add the entry.**

```jsonc
// src/DataAcquisition.Cli/appsettings.json
{
  "Cik": "2099048",
  "Name": "GM Financial Consumer Automobile Receivables Trust 2026-1",
  "CreditTier": "Prime"
}
```

`Cik` may be zero-padded or not. `CreditTier` is free text and appears in the report as a
badge; it is what explains why one pool's 30+ delinquency is 0.97% and another's is 24.43%.

**4. Ingest and report.**

```bash
dotnet run --project src/DataAcquisition.Cli -- ingest --subject 2099048 --periods 3
dotnet run --project src/DataAcquisition.Cli -- report --out ./site
```

**5. Verify.** A trust with ingested data but no configuration entry, or the reverse, is logged
as a warning and still appears in the report rather than vanishing from a page that claims to
cover the data.

```bash
dotnet run --project src/DataAcquisition.Cli -- status --limit 10
```

---

## Run the production pipeline by hand

```bash
gh workflow run production-run.yml --ref main -f periods=3
gh run watch "$(gh run list --workflow production-run.yml --limit 1 --json databaseId --jq '.[0].databaseId')"
```

It refuses any ref but `main`. The `deploy` job depends on `ingest`, so a run with a failed
source publishes nothing. Logs are uploaded as the `ingestion-log` artifact for 30 days.

To force a full re-ingest in production:

```bash
gh workflow run production-run.yml --ref main -f periods=3 -f force=true
```

---

## Refresh the performance baseline

Part of every release, and the fix for a baseline captured on the wrong machine.

```bash
gh workflow run capture-baseline.yml --ref main
# wait, then:
gh run download "$(gh run list --workflow capture-baseline.yml --limit 1 --json databaseId --jq '.[0].databaseId')" \
  --name perf-baseline --dir /tmp/baseline

git checkout develop && git pull
git checkout -b chore/refresh-perf-baseline
cp /tmp/baseline/baseline.json benchmarks/baseline.json
git commit -am "chore: refresh the perf baseline from main"
git push -u origin chore/refresh-perf-baseline
gh pr create --base develop
```

The workflow uploads an artifact rather than pushing, because `main` is protected and a
workflow pushing to it would bypass the review the protection exists for.
