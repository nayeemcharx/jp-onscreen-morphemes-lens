# LLM Lookup Server

FastAPI server that returns the hiragana reading and English meaning of a Japanese word by calling the Claude API.  Results are cached locally in a SQLite database (`word_cache.db`), so the same word is only sent to Claude once.

---

## Setup

```bash
cd llm-server
pip install -r requirements.txt
```

Set your Anthropic API key:

```powershell
# PowerShell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

```bash
# bash / zsh
export ANTHROPIC_API_KEY="sk-ant-..."
```

## Start the server

```bash
uvicorn main:app --host 127.0.0.1 --port 8100
```

Keep this terminal open while using the OCR overlay.

---

## API

### `GET /lookup/{word}`

Look up a single Japanese word.

**Response**

```json
{
  "hiragana": "にほんご",
  "meaning": "Japanese language",
  "lookup_count": 3
}
```

| Field | Description |
|---|---|
| `hiragana` | Hiragana reading of the word |
| `meaning` | Concise English meaning (1–2 senses) |
| `lookup_count` | How many times this word has been looked up |

### `GET /health`

Returns `{"status": "ok"}` — use to check if the server is running.

---

## Cache

Results are stored in `word_cache.db` (SQLite) in the same directory.  The cache key is the SHA-256 hash of the UTF-8-encoded word, so the DB never stores anything the Claude API didn't already receive.

## Model

Uses `claude-3-haiku-20240307` — the cheapest Claude model, fast and sufficient for single-word lookups.
