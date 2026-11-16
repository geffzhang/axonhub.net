# Porting guard scripts

Vendored copies of the `cross-lang-porting` skill scripts (standard library
only), so CI and local checks do not depend on the skill directory.

| Script | Purpose |
|---|---|
| `diff_register.py` | `lint` the difference ledger, `render` `02-differences.md`, `new` entries |
| `diff_corpus.py` | `record` / `verify` the differential corpus under `corpus/` |
| `reference_lock.py` | `lock` / `check` the pinned upstream reference |

Typical local commands (from the repository root):

```powershell
python -I porting/scripts/diff_register.py lint --dir porting
python -I porting/scripts/reference_lock.py check --repo E:\GitHub\axonhub-ref --dest porting/reference-lock.json

dotnet build tools/AxonHub.CorpusRunner -c Release
python -I porting/scripts/diff_corpus.py verify `
  --cmd "dotnet tools/AxonHub.CorpusRunner/bin/Release/net10.0/AxonHub.CorpusRunner.dll --mode upstream-request" `
  --corpus corpus/upstream-request
```

`02-differences.md` is generated between the `<!-- RENDER:START -->` and
`<!-- RENDER:END -->` markers; edit `differences.jsonl`, then run `render`.
