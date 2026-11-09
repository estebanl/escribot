# FluentEscritor

**FluentEscritor** is an Academic IELTS Writing Coach & Authentic Computer-Delivered (Inspera) Exam Simulator built to help writers break past the **Band 6.5 → 7.5/8.0+ plateau**, synced across devices on Vercel + Turso.

## Core Capabilities

- **Dual Writing Modes**
  - **Editorial Coach Studio**: 5-minute interactive Outline Check (validating Task 1 number-free macro Overviews & Task 2 Thesis/paragraph structure before writing), distraction-free serif/sans editor, and surgical on-demand tools (**Academic Collocations & Hedging**, **Spanish → English L1 Interference Scan**, **Single-Paragraph Check**) with strictly zero ghost-text autocomplete.
  - **Authentic Inspera Exam Simulator**: Faithful Computer-Delivered IELTS replica supporting **20m Task 1**, **40m Task 2**, and **60m Shared-Clock Full Mock Exams** (`Part 1` / `Part 2` switcher tabs, `1:2` weighted overall Writing Band `(T1 + 2 × T2) / 3`, prompt yellow highlighter, candidate scratchpad notes, spellcheck disabled).
- **Full Academic Task 1 & Task 2 Coverage**
  - Pre-seeded library spanning **all 7 Task 1 visual archetypes** (Line Graph, Bar Chart, Comparative Pie Charts, Data Table, Mixed Dual-Axis Charts, Process Diagram, Two-Period Comparison Map) and **all 5 Task 2 essay archetypes**.
  - **Screenshot Upload (`Ctrl/Cmd+V`)**: Paste or drop chart screenshots from external prep books (e.g., Cambridge IELTS); client-side WebP compression stores images directly in Turso without external S3 buckets.
  - **AI Prompt & Chart Generator**: Generates fresh structured visual datasets and discursive prompts on demand.
- **Post-Submission Diagnostic & Multi-Draft (`v1 → v2 → v3`) Loop**
  - Official 4-criterion scoring (`TA/TR`, `CC`, `LR`, `GRA` in `0.5` steps), interactive color-coded inline span annotations, toggleable *"Your Ideas at Band 8.0+"* side-by-side diff, and iterative `v2/v3` re-scoring with band delta tracking.
- **Between-Essay Retention**
  - Auto-populated **Personal Error & Academic Collocation Vault** (with spaced-recall Flashcard Deck) + **5-Minute Targeted Micro-Drills** (`4m Overview Sprint`, `Cohesion De-Mechanizer`, `L1 Surgeon`, `Lexical & Hedging Upgrade`).
- **Multi-Provider AI Routing (OpenRouter Unified Default)**
  - Routes requests through OpenRouter's unified API using **Claude Sonnet 5.5** for high-precision 4-criterion IELTS rubric scoring, and **DeepSeek V4.1 Flash** for multimodal chart screenshot extraction, outlines, and 5-minute micro-drills (~$1.70/month total under heavy practice). Automatic failover fallback arrays, Zero Data Collection policy, and real-time USD spend tracking. Direct **DeepSeek**, **Google Gemini**, **OpenAI**, or **Anthropic Claude** keys remain fully supported in Settings (`AES-256-GCM` encrypted) or environment variables.

---

## Local Development

```bash
npm install
cp .env.example .env.local
npm run dev
```

By default in local development, `FluentEscritor` automatically creates and seeds `file:./local.db` (SQLite/LibSQL) on first boot—no manual migration command required.

---

## Deploying to Vercel (Cross-Device Sync)

1. **Create a free Turso database**:
   ```bash
   turso db create fluentescritor
   turso db show fluentescritor --url
   turso db tokens create fluentescritor
   ```
2. **Import the repository into Vercel** and set these Environment Variables:
   - `APP_PASSCODE` — Your personal passcode to unlock the workspace across laptop, tablet, and phone (protected by an `HttpOnly` HMAC-signed cookie).
   - `TURSO_DATABASE_URL` — `libsql://...` URL from Turso.
   - `TURSO_AUTH_TOKEN` — Auth token from Turso.
   - `OPENROUTER_API_KEY` — Your OpenRouter API key from `openrouter.ai` (or configure keys inside the encrypted **Settings** UI after unlocking).
   - *(Optional)* `DEEPSEEK_API_KEY`, `GEMINI_API_KEY`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `APP_SECRET`.
3. **Deploy**: Tables and the curated Academic IELTS starter bank initialize idempotently on first request.
