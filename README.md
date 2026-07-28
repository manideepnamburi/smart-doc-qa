# SmartDocQA — Hybrid RAG Document Q&A with Knowledge Graph

**Stack:** C# · .NET 8 · Claude API · Qdrant · Neo4j · BM25 · Azure OpenAI · Azure Document Intelligence

A working Retrieval-Augmented Generation system that answers questions over PDF and Word documents
using **hybrid retrieval** (dense vector + keyword search, fused with RRF), a **Claude-as-reranker**
pass, **Neo4j knowledge-graph** traversal for relationship-style queries, and **multi-modal ingestion**
(tables via Azure Document Intelligence, charts via Claude Vision).

Built as a personal deep-dive into production RAG architecture — Clean Architecture throughout,
config-driven feature flags, and provider-agnostic embeddings (swap Azure OpenAI / OpenAI / Ollama
via one config value, zero code changes).

---

## What's actually implemented (Phases 1–5)

| Phase | Feature | Status |
|-------|---------|--------|
| 1 | Core RAG — PDF/Word ingestion, Azure OpenAI embeddings, Qdrant dense retrieval, Claude synthesis with citations | ✅ Done |
| 2 | Hybrid indexing — BM25 keyword search + Reciprocal Rank Fusion | ✅ Done |
| 3 | Query rewriting, smart folder ingest/delete (priority-based), SQLite document registry | ✅ Done |
| 4 | Claude-as-reranker, table extraction (Azure Doc Intelligence), chart extraction (PDFtoImage + Claude Vision) | ✅ Done |
| 5 | Neo4j knowledge graph — Claude-based entity extraction, Cypher MERGE ingestion, graph-augmented retrieval | ✅ Done |
| 6 | Repo hygiene, unit tests, RAG eval harness (LLM-as-judge) | 🏗️ In progress |
| 6.5–12 | Guardrails, chat UI, agentic mode + MCP server, dual-mode local/Azure config, Redis cache, Azure deployment + LLMOps, multi-tenancy, auth, UI polish | 📋 Planned |

Real numbers from ingesting a 67-page NHANES health survey PDF: **688 entities extracted → ~335 unique
Neo4j nodes → 552 relationships**, alongside the standard dense+BM25 chunk index.

---

## Architecture

```
┌──────────────────────────────────────────────────────────┐
│                   SmartDocQA.API                         │
│         ASP.NET Core Web API  +  Swagger UI              │
└──────────────┬───────────────────────────────────────────┘
               │ uses
┌──────────────▼───────────────────────────────────────────┐
│               SmartDocQA.Application                      │
│   IngestDocumentUseCase | IngestFolderUseCase              │
│   QueryDocumentUseCase  | DeleteDocumentsUseCase            │
│   AppOptions (strongly-typed config classes)               │
└──────────────┬───────────────────────────────────────────┘
               │ depends on interfaces from
┌──────────────▼───────────────────────────────────────────┐
│                SmartDocQA.Domain                          │
│   Interfaces | Models | Enums                              │
│   (zero external dependencies — the dependency rule)        │
└──────────────▲───────────────────────────────────────────┘
               │ implements
┌──────────────┴───────────────────────────────────────────┐
│             SmartDocQA.Infrastructure                     │
│                                                            │
│  Parsing:        PdfPig | OpenXml (Word)                   │
│  Chunking:        FixedSize (token-based, overlap)          │
│  Embeddings:      Factory — AzureOpenAI | OpenAI | Ollama    │
│                   (swap provider via one config value)       │
│  VectorStore:     Qdrant, via SK's Qdrant connector          │
│  KeywordIndex:    BM25 — hand-implemented Okapi BM25 over a  │
│                   SQLite inverted index (own design, see     │
│                   below — not the SK ranker package)          │
│  Fusion:          RRF (Reciprocal Rank Fusion, k=60)          │
│  Reranking:       Claude-as-reranker                          │
│  GraphStore:      Neo4j — Claude entity extraction → Cypher   │
│                   MERGE, reference-counted deletion           │
│  Multi-modal:     Azure Doc Intelligence (tables),             │
│                   Claude Vision (charts, via PDFtoImage)       │
│  LLM:             Direct Claude HTTP client — no SDK,          │
│                   Polly retry policies                         │
│  Registry:        SQLite — duplicate prevention, priority-     │
│                   based smart folder ingest/delete             │
│  Prompts:         File-based templates, hot-editable           │
└────────────────────────────────────────────────────────────┘
```

**Query pipeline:** rewrite → parallel retrieval (dense + BM25 + graph) → RRF fusion →
similarity threshold filter → Claude reranker → synthesis with citations.

---

## Design decision: BM25 is hand-implemented, not a package

`BM25KeywordIndex.cs` implements Okapi BM25 from scratch — standard IDF with
Robertson–Sparck-Jones smoothing, plus term-frequency saturation and length
normalization (`K1 = 1.5`, `B = 0.75`, the standard defaults) — backed by a SQLite
inverted index rather than an in-memory dictionary or a JSON file.

That storage choice was deliberate, not incidental: a JSON-file index has to be
rewritten in full on every document delete, which measured at **~22 seconds at
600,000 chunks**. The SQLite version does indexed deletes instead, measured at
**5–50ms regardless of total corpus size**. The query layer is also written against
`System.Data.Common.DbConnection` rather than `SqliteConnection` directly, and every
multi-word lookup is a single batched query — so the same code stays fast if the
backing store is ever swapped to a networked database like Azure SQL, with zero
changes to the scoring logic.

`Microsoft.SemanticKernel.Connectors.Qdrant` is genuinely used for dense retrieval,
but `SemanticKernel.Rankers.BM25` / `.Pipelines` were evaluated and **not** used —
most lightweight BM25 packages assume an in-memory index, which would have given up
the scaling property above. The hand-rolled version was kept as the better-engineered
fit for this specific requirement, not as unfinished work.

---

## Feature Flags (`appsettings.json` → `RAG` section)

Turn features on/off without touching code:

| Flag | Controls |
|------|----------|
| `UseHybridIndex` | BM25 keyword search alongside dense vector retrieval |
| `UseReranking` | Claude-as-reranker pass over fused results |
| `UseGraphRetrieval` | Neo4j graph traversal alongside dense/sparse retrieval |
| `UseTableExtraction` | Azure Document Intelligence table extraction |
| `UseChartExtraction` | Claude Vision chart summarization |
| `UseQueryRewriting` | Query expansion before retrieval |
| `FallbackToLLM` | Answer from model knowledge if retrieval finds nothing above threshold |

---

## Document Sources

| Source | How to use |
|--------|-----------|
| **Local file** | `POST /api/documents/ingest/local` `{ "path": "C:/Docs/report.pdf" }` |
| **Local folder** | Smart priority ingest: specific files → file types → ingest all |
| **Azure Blob** | `POST /api/documents/ingest/blob` `{ "blobPath": "reports/Q3.pdf", "containerName": "documents" }` |

Supported formats: `.pdf`, `.docx`, `.doc`

---

## Quick Start

### 1. Prerequisites
- Docker Desktop running
- .NET 8 SDK
- Anthropic API key
- Azure OpenAI resource (for embeddings) — or set `Embedding:Provider` to `Ollama` for a free local alternative

### 2. Start infrastructure
```bash
docker-compose up -d
```
- Qdrant dashboard: http://localhost:6333/dashboard
- Neo4j browser: http://localhost:7474

### 3. Configure secrets
Copy the template and fill in your own keys — **never edit `appsettings.json` directly with real keys**:
```bash
cd src/SmartDocQA.API
cp appsettings.Development.json.template appsettings.Development.json
```
Then open `appsettings.Development.json` and replace every `YOUR_...` placeholder with your real
values (Anthropic API key, Azure OpenAI key/endpoint, Azure Document Intelligence key, Neo4j password).
This file is gitignored — it never leaves your machine.

### 4. Run
```bash
dotnet run
```
Swagger UI: `https://localhost:7XXX/swagger`

### 5. Try it
```bash
# Ingest a local PDF
curl -X POST https://localhost:7XXX/api/documents/ingest/local \
  -H "Content-Type: application/json" \
  -d '{"path": "C:/Docs/AnnualReport.pdf"}'

# Ask a question
curl -X POST https://localhost:7XXX/api/documents/query \
  -H "Content-Type: application/json" \
  -d '{"question": "What was the Q3 net revenue?", "useGraph": false, "useReranking": true}'
```

---

## Prompt Templates

All prompts live in `src/SmartDocQA.API/Prompts/` — edit without recompiling:

| File | Purpose |
|------|---------|
| `qa_system.txt` | Main QA system prompt |
| `qa_user.txt` | QA user prompt — `{{context}}` and `{{question}}` variables |
| `rerank_system.txt` | Claude reranker scoring prompt |
| `entity_extraction.txt` | NER prompt for knowledge graph population |
| `query_rewrite.txt` | Query expansion prompt |

---

## Known Issues / Cleanup Debt

Being upfront about what's rough around the edges:

- **`InfrastructureStubs.cs`** contains the real, working Qdrant vector store adapter and dense
  retriever — not stubs. Left over from early scaffolding; naming will be split into properly
  named files (`Neo4jGraphStore.cs`, `QdrantVectorStoreAdapter.cs`, etc.) during the Phase 6 cleanup pass.
- **Graph-chunk reranking bias:** chunks describing graph relationships in arrow notation
  (`X --published_by--> Y`) consistently score lower with the Claude reranker than natural-prose
  chunks, even when the graph data is more accurate. Planned fix: rewrite graph descriptions as
  natural-language sentences before reranking.
- **Prompt file path resolution** depends on the app running from its own base directory (handled
  via `AppContext.BaseDirectory` with a working-directory fallback) — solid for `dotnet run`,
  Visual Studio, and published builds, but worth hardening further before containerization (Phase 9).

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.SemanticKernel` | Core AI framework |
| `Microsoft.SemanticKernel.Connectors.Qdrant` | Qdrant vector store connector (in active use) |
| `Microsoft.SemanticKernel.Connectors.OpenAI` | OpenAI connector |
| `Azure.AI.OpenAI` | Azure OpenAI embeddings |
| `OllamaSharp` | Local embedding provider (free alternative) |
| `UglyToad.PdfPig` | PDF text extraction |
| `DocumentFormat.OpenXml` | Word document parsing |
| `PDFtoImage` | PDF page rasterization for chart extraction |
| `Azure.AI.FormRecognizer` | Table extraction (Azure Document Intelligence) |
| `Azure.Storage.Blobs` | Azure Blob document source |
| `Azure.Identity` | Azure auth |
| `Neo4j.Driver` | Knowledge graph database |
| `Microsoft.Data.Sqlite` | Document registry + BM25 inverted index (see design decision above) |
| `Microsoft.Extensions.Http.Resilience` | Polly retry policies for Claude HTTP client |

---

## Next Up

Phase 6 in progress: repository hygiene (done), unit tests for RRF fusion / chunking / priority
ingest logic, and a RAG evaluation harness — a golden dataset of ~30–50 NHANES Q&A pairs scored via
LLM-as-judge on retrieval hit-rate, answer faithfulness, and citation accuracy, gating each future
phase merge.
