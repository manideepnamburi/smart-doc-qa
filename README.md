# SmartDocQA — Advanced RAG Document Q&A Agent

**Stack:** C# · .NET 8 · Semantic Kernel · Claude API · Qdrant · Neo4j · BM25

A production-grade Advanced RAG system with hybrid retrieval, reranking, graph-enhanced search,
and multi-modal document ingestion (PDF, Word, tables, charts, scanned pages).

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
│   IngestDocumentUseCase  |  QueryDocumentUseCase          │
│   AppOptions (strongly-typed config classes)              │
└──────────────┬───────────────────────────────────────────┘
               │ depends on interfaces from
┌──────────────▼───────────────────────────────────────────┐
│                SmartDocQA.Domain                          │
│   Interfaces  |  Models  |  Enums                        │
│   (zero external dependencies)                            │
└──────────────▲───────────────────────────────────────────┘
               │ implements
┌──────────────┴───────────────────────────────────────────┐
│             SmartDocQA.Infrastructure                     │
│                                                          │
│  DocumentSources:  Local | AzureBlob | OneDrive          │
│  Parsing:          PdfPig | OpenXml (Word)               │
│  Chunking:         FixedSize (Semantic coming Phase 2)    │
│  VectorStore:      Qdrant (SK connector)                  │
│  KeywordIndex:     BM25 (SemanticKernel.Rankers.BM25)    │
│  GraphStore:       Neo4j                                  │
│  Retrieval:        Dense | Sparse | Graph                 │
│  Fusion:           RRF (Reciprocal Rank Fusion)           │
│  Reranking:        Claude-as-reranker                     │
│  LLM:              Claude HTTP client (direct API)        │
│  Prompts:          File-based templates                   │
└──────────────────────────────────────────────────────────┘
```

---

## Feature Flags (appsettings.json → RAG section)

Turn features on/off without touching code:

| Flag | Default | Controls |
|------|---------|----------|
| `UseHybridIndex` | true | BM25 keyword search alongside vector |
| `UseReranking` | false | Claude reranker (enable Phase 4) |
| `UseGraphRetrieval` | false | Neo4j graph traversal (enable Phase 5) |
| `UseTableExtraction` | false | Azure Doc Intelligence tables |
| `UseChartExtraction` | false | Claude Vision chart summarization |
| `UseQueryRewriting` | true | Query expansion before retrieval |

---

## Document Sources

| Source | How to use |
|--------|-----------|
| **Local file** | `POST /api/documents/ingest/local` `{ "path": "C:/Docs/report.pdf" }` |
| **Azure Blob** | `POST /api/documents/ingest/blob` `{ "blobPath": "reports/Q3.pdf", "containerName": "documents" }` |
| **OneDrive** | `POST /api/documents/ingest/onedrive` `{ "itemId": "01ABC...", "fileName": "report.pdf" }` |

Supported formats: `.pdf`, `.docx`, `.doc`

---

## Quick Start

### 1. Prerequisites
```
- Docker Desktop running
- .NET 8 SDK
- Anthropic API key
```

### 2. Start infrastructure
```bash
docker-compose up -d
```

Verify:
- Qdrant dashboard: http://localhost:6333/dashboard
- Neo4j browser:    http://localhost:7474

### 3. Configure
Edit `src/SmartDocQA.API/appsettings.json`:
```json
{
  "Anthropic": { "ApiKey": "sk-ant-YOUR_KEY_HERE" }
}
```

### 4. Run
```bash
cd src/SmartDocQA.API
dotnet run
```

Swagger UI: https://localhost:7XXX/swagger

### 5. Test
```bash
# Ingest a local PDF
curl -X POST https://localhost:7XXX/api/documents/ingest/local \
  -H "Content-Type: application/json" \
  -d '{"path": "C:/Docs/AnnualReport.pdf"}'

# Ask a question
curl -X POST https://localhost:7XXX/api/documents/query \
  -H "Content-Type: application/json" \
  -d '{"question": "What was the Q3 net revenue?", "useGraph": false, "useReranking": false}'
```

---

## Build Phases

| Phase | Feature | Week | Status |
|-------|---------|------|--------|
| 1 | Classic RAG — PDF/Word ingest + Claude Q&A | 1–2 | 🏗️ Scaffold done — wire Qdrant |
| 2 | Hybrid indexing — BM25 + RRF fusion | 3 | 📋 Interfaces ready |
| 3 | Reranking — Claude-as-reranker | 4 | 📋 Stub ready |
| 4 | Citations + multi-modal (tables, charts) | 5 | 📋 Stub ready |
| 5 | Neo4j graph store + entity extraction | 6–8 | 📋 Stub ready |
| 6 | Polish — logging, retry, cache, tests | 9–10 | 📋 |

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.SemanticKernel` | AI framework |
| `Microsoft.SemanticKernel.Connectors.Qdrant` | Vector store connector |
| `SemanticKernel.Rankers.BM25` | BM25 keyword ranking |
| `SemanticKernel.Rankers.Pipelines` | BM25 → LM reranker pipeline |
| `UglyToad.PdfPig` | PDF text extraction |
| `DocumentFormat.OpenXml` | Word document parsing |
| `Azure.AI.FormRecognizer` | Table extraction |
| `Azure.Storage.Blobs` | Azure Blob source |
| `Microsoft.Graph` | OneDrive source |
| `Neo4j.Driver` | Graph database |
| `Microsoft.Extensions.Http.Resilience` | Polly retry policies |

---

## Prompt Templates

All prompts live in `/Prompts/` — edit without recompiling:

| File | Purpose |
|------|---------|
| `qa_system.txt` | Main QA system prompt |
| `qa_user.txt` | QA user prompt with `{{context}}` and `{{question}}` variables |
| `rerank_system.txt` | Claude reranker scoring prompt |
| `entity_extraction.txt` | NER prompt for graph population |
| `query_rewrite.txt` | Query expansion prompt |

---

## Next Step — Phase 1 Completion Checklist

```
□ Replace ClaudeEmbeddingService dummy vectors with real embeddings
  → Option A: OpenAI text-embedding-3-small (cheap, fast)
  → Option B: Ollama all-minilm (free, local, via Microsoft.Extensions.AI)

□ Wire up QdrantVectorStoreAdapter (remove [STUB] comments)
  → Install: Microsoft.SemanticKernel.Connectors.Qdrant --prerelease
  → Follow: https://learn.microsoft.com/semantic-kernel/concepts/vector-store-connectors/qdrant-connector

□ Test end-to-end: ingest a PDF → ask a question → get a cited answer
```
