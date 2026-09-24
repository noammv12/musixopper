# Palon: Memory, Template Learning, Call Coaching (design, rev 2)

Target: C#/.NET 8 WPF, local-first. Tags: **VERIFIED** (repo path or URL; repos cloned at HEAD on 2026-09-24 under `research/repos/`) or **CORRECTED**.

## A) Memory — what the projects actually do today

### mem0 (Apache-2.0, VERIFIED)
- **CORRECTED, important.** The current `Memory.add` pipeline is **additive-only** ("V3 phased batch pipeline", `mem0/memory/main.py:915-1060`). It no longer runs the ADD/UPDATE/DELETE/NONE step. `DEFAULT_UPDATE_MEMORY_PROMPT` still exists in `mem0/configs/prompts.py:176` but nothing in `mem0/` calls it.
- The current flow, worth copying almost line for line:
  1. **Context:** the last 10 session messages + the new messages.
  2. **Retrieve:** vector top_k=10 existing memories, scoped by user_id, agent_id and run_id.
  3. **Anti-hallucination id mapping:** existing UUIDs are replaced with `"0","1",...` before the prompt and mapped back afterwards (`uuid_mapping`). Port this: LLMs corrupt GUIDs.
  4. **One LLM call** with `ADDITIVE_EXTRACTION_PROMPT` (prompts.py:468) and `response_format=json_object`. The prompt tells the model that its "sole operation is ADD". It extracts from *both* user and assistant turns ("User was recommended X"), but not assistant pleasantries. Existing memories are used *only for dedup and linking*: related ones go into `linked_memory_ids`. It also says that a known entity ≠ a known fact ("User has a dog Max" vs. "camping trip with Max" are distinct).
  5. **Batch embed**, then **MD5 hash dedup** against the existing results and within the batch, then insert. It stores `text_lemmatized` for BM25 (so hybrid search is built in).
  6. If the LLM fails, it raises an error (`LLMError`) and does not return `[]`. The source comment explains that a silent empty list hides 429s. Palon's fallback chain needs the same distinction.
- The legacy UPDATE prompt is still a good template for Palon's *user-profile* reconcile. It uses few-shot examples per event, "keep the same ID", "return IDs from input only", and gives `old_memory` for UPDATE.

### Letta (Apache-2.0)
- **CORRECTED.** `letta-ai/letta` main is now just a landing page. The old server (core memory blocks, `core_memory_append/replace`) sits on an `archive` branch, and AGENTS.md says not to copy it. The current code is **`letta-ai/letta-code`** (TypeScript, Apache-2.0).
- Current model (VERIFIED `src/tools/descriptions/Memory.md`, `MemoryV2.md`, `src/agent/memory-filesystem.ts`):
  - Memory is a **git-backed directory of Markdown files** with a frontmatter `description`.
  - Files in `system/` (v2: root `.md` files except `MEMORY.md`) are **compiled into the system prompt**, meaning they are always in context.
  - Other files show only as a path + description tree (bounded by `memfsTreeMaxLines/Chars`) and are read on demand.
  - A single `memory` tool has commands `str_replace | insert | delete | rename | update_description | create`, and each call takes a required **`reason`**, which becomes the git commit message.
  - Background consolidation runs in a **memory-worker subagent** in a separate worktree (`src/agent/subagents/memory-worker.ts`), then gets merged and the system prompt recompiled.
- **For Palon:** "About you" = `profile/*.md` pinned files, always injected. The agent edits them through `str_replace`-style operations with a `reason`, and every change is an undoable, versioned entry (plain file versions, no git needed). This is more robust than JSON fact lists for small models, because edits are local text replacements.

### Graphiti (Apache-2.0, VERIFIED `graphiti_core/utils/maintenance/edge_operations.py`, `prompts/dedupe_edges.py`)
- **Bitemporal: four timestamps**, not two. `valid_at`/`invalid_at` = when the fact was true in the world. `created_at`/`expired_at` = when the system learned or retired it.
- **Resolve prompt:** give the new fact, "EXISTING FACTS" (for dedup) and "FACT INVALIDATION CANDIDATES" with *continuous idx numbering* across both lists. It returns `{duplicate_facts:[idx], contradicted_facts:[idx]}`. It includes "NEVER mark facts as duplicates if they have key differences, particularly around numeric values, dates" (important for budgets).
- **Invalidation algorithm** (`resolve_edge_contradictions`, line 538):
  - For each contradicted old fact, skip it if their validity intervals don't overlap.
  - Otherwise, if old.valid_at < new.valid_at, set `old.invalid_at = new.valid_at` and `old.expired_at = now`.
  - Conversely (lines 826-839), if a candidate has a *later* valid_at than the new fact, the *new* fact is expired immediately. This handles stale info that arrives late.
- **Port to C#:** use a `ClientFact` table with those four columns. Only facts where `invalid_at IS NULL AND expired_at IS NULL` get injected.

### LangMem (MIT, VERIFIED `src/langmem/knowledge/extraction.py`, `reflection.py`, docs `guides/manage_user_profile.md`)
- **Profile vs. semantic:** it is the same manager with different flags. A **profile** is one Pydantic schema with `enable_inserts=False` ("only ever manage a single instance", updated in place). **Semantic** memory is a collection with inserts allowed. `enable_deletes` defaults to **False**.
- The manager is a tool-calling loop (`max_steps`, default 1) over `schemas + Done`, with `RemoveDoc` for deletes. The instruction prompt (`_MEMORY_INSTRUCTIONS`, line 185) asks to "caveat uncertain information with confidence p(x)", "quote supporting information", and "consolidate and compress redundant memories".
- **Background extraction = debounce.** `ReflectionExecutor.submit(payload, after_seconds=N, thread_id)` *cancels any pending task for the same thread* and requeues (lines 308-328). This is the right trigger for Palon: after each Ask-Palon exchange, submit with 60-120 s delay, so extraction runs once when the conversation goes quiet.
- `short_term/summarization.py` keeps a `RunningSummary` and summarizes after `max_tokens_before_summary`. See agent_chat.md.

## B) Palon memory design (revised)

```
%LocalAppData%/Palon/memory/
  profile/about.md, rules.md, style.md   # pinned, always injected (Letta-code style), DPAPI-encrypted
  profile/.history/                      # every edit: {ts, reason, before, after} -> undo
  clients.db                             # SQLite: ClientFact(bitemporal) + FTS5 + embedding BLOB
```
- **User memory writes:**
  - An explicit "remember that..." goes directly to the `memory_edit` tool (`str_replace/insert`, required `reason`), followed by a toast with Undo.
  - Inferred facts come from the debounced background job and go to a *pending* list shown as suggestions, never written straight into `profile/`.
  - Structured rules (`no_call_before 12:00` for Dani) are **also** mirrored into a typed `rules.json` that the scheduler enforces in code. This is unchanged.
- **Client memory write**, per call, inside the summary call (same schema) followed by one reconcile call:
  1. `client_facts[] {type, text, quote, valid_at?}`.
  2. Candidates are that client's active facts (small, no vector search needed), shown to the model with integer ids (mem0 trick).
  3. Graphiti-style prompt returns `duplicate_facts`/`contradicted_facts`.
  4. Apply the four-timestamp algorithm, plus MD5 dedup.
- **Retrieval:**
  - MVP: FTS5 BM25 per client, with the Hebrew normalization from rev 1 (strip niqqud, final letters, prefixes ו/ה/ב/ל/מ/ש/כ). mem0 also keeps a lemmatized copy for BM25, so store `text_norm`.
  - v2: hybrid BM25 + cosine with RRF (k=60).
- **Embeddings, multilingual-e5-small** (VERIFIED via the HF card through search; direct fetch was blocked):
  - MIT, 384-dim, 12 layers.
  - **Prefixes are required:** `"query: "` for queries, `"passage: "` for stored facts. Mean-pool and L2-normalize.
  - ONNX is available through `optimum-cli export onnx --task feature-extraction` (the HF repo also ships an `onnx/` folder; `[verify]` which files).
  - Tokenizer: XLM-R SentencePiece. `Microsoft.ML.Tokenizers` can load the `sentencepiece.bpe.model` `[verify API]`.
  - Hebrew quality is decent for its size (MIRACL/Mr.TyDi multilingual training). Validate on 50 of the user's own queries before shipping.

## C) Template learning
Unchanged from rev 1: send-event logging, DiffPlex edit mining (3+ repeats → proposal), clustering untemplated sends, Wilson lower bound once n≥20, proposal cards with rejection memory, versioned templates. The one addition is the mem0 hash-dedup idea, applied so the same proposal is never re-suggested: hash the normalized diff.

## D) Call coaching

### VAD — Silero (VERIFIED `silero-vad/LICENSE` MIT; `src/silero_vad/data/*.onnx`)
- ONNX models ship in-repo: `silero_vad.onnx` (2.3 MB), plus `_16k_op15`, `_half`, `_op18_ifless` variants.
- **Official C# example exists:** `examples/csharp/SileroVadOnnxModel.cs` + `SileroVadDetector.cs` (Microsoft.ML.OnnxRuntime). Port it directly.
- I/O contract:
  - Chunks: 16 kHz → **512 samples** (8 kHz → 256).
  - Prepend a **context of the previous 64 samples** (32 at 8 kHz), so the model sees 576.
  - Inputs `input`, `state` float[2,1,128], `sr`. Outputs `output` (probability) and `stateN`, which is fed back.
  - State is per stream, so the mic and loopback need **separate model state**.
- Segmenting parameters (defaults in `utils_vad.py:283-295`):
  - threshold 0.5, `neg_threshold = threshold-0.15` (hysteresis)
  - min_speech 250 ms, min_silence 100 ms, speech_pad 30 ms
  - For turn-taking metrics use min_silence ≈ 300-500 ms, otherwise every breath splits a turn.
- Metric definitions are unchanged from rev 1: talk ratio, longest monologue, overlap, WPM, response latency, questions. The Gong 43:57 figure is an unverified blog number, so present it as a "reference" only.

### LLM per call
Unchanged from rev 1: one structured call with enums, a required `quote` per item, a fuzzy check that the quote appears in the transcript, a schema validator, one retry that includes the error, temperature 0.

### Weekly
Log-odds with a Dirichlet prior (Monroe et al. 2008), at least 8 occurrences; best call of the week; local HTML report.

## E) Gemini constraints (affect everything)
- **Data use, VERIFIED** (https://ai.google.dev/gemini-api/terms, via search; direct fetch blocked): on the **unpaid** tier Google uses prompts and outputs to improve products, **human reviewers may read them**, and the terms say "don't submit sensitive, confidential, or personal information". Paid use is excluded from improvement. Client call transcripts are personal data, so **the free tier is not appropriate for call summaries, client memory or coaching.** Palon should default to the paid Gemini key or DeepSeek (check DeepSeek's own terms), or at least warn the user and get consent.
- **Rate limits, CORRECTED/uncertain:** the rate-limits page couldn't be fetched. Third-party 2026 summaries report the newest Flash models (3.5-3.8 Flash) at about **20 requests/day** on free tier, while older 3 Flash / 2.5 Flash sit at 10 RPM / 250k TPM / 500-1,500 RPD. Limits are **per project**, and RPD resets at midnight Pacific. At about 100 calls/day, the free tier *cannot* cover even one call per recording on gemini-3.8-flash. Check the AI Studio quota page for the actual project.

## F) Portable C# pieces (summary)
1. `IdMap`: GUID↔small-int mapping around every LLM prompt that references stored items (mem0).
2. `FactResolver`: Graphiti prompt + four-timestamp invalidation.
3. `DebouncedReflector`: per-thread cancel-and-requeue after N seconds (LangMem).
4. `MemoryFiles`: pinned Markdown blocks + `str_replace/insert/create/delete` tool with required `reason` + history (letta-code).
5. `SileroVad`: port of the official C# example, one instance per channel.

## Phased plan
- **MVP:** profile Markdown blocks + memory_edit tool + undo; client facts with bitemporal invalidation in SQLite FTS5; Silero metrics; objection schema; paid-key/consent gate.
- **v2:** debounced inferred suggestions; e5 hybrid retrieval; template clustering and outcome stats.
- **v3:** coaching drills.

## C) Template learning — as built (v1)
- Log: `%LOCALAPPDATA%\Palon\template_learning.json` (`TemplateLearningStore`). Every copy from Dock ("העתק תבנית"), Today and Templates is logged with template id + version, client name/phone, time, and the final text when it was changed in Palon's "שנה והעתק" / "הודעה חופשית" sheet. Edits made later inside WhatsApp are not visible.
- Outcomes: deposit = a deal in SalesStore for the same client (name match: equal or same first two words) within 14 days; next call = a CallStats record with the same number (last 9 digits) within 14 days. WhatsApp replies are not observable. Sends from the Templates screen carry only the typed first name and no phone, so they rarely link.
- Edit mining: name-neutral line LCS diff (own implementation, no DiffPlex), hashed signature per template version; 3+ identical edits → proposal card with a red/green diff (accept / edit first / reject). Decisions are remembered by key, never re-suggested.
- Free-text clustering: word-set Jaccard ≥ 0.5, single link, 3+ → "new template?" card with the most typical message, name removed.
- Stats on a template card only after 20 uses: uses · deposits within 14d · rate (Wilson lower bound in tooltip).
- Templates carry `Version` + `History` (last 30); the editor lists earlier versions with restore.
