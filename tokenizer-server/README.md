# Japanese Tokenizer Server

FastAPI server that exposes a `/tokenize` endpoint backed by **SudachiPy** (split-mode C).

## Requirements

- Python 3.10+
- pip

## Setup

```bash
cd tokenizer-server
python -m venv .venv
# Windows
.venv\Scripts\activate
# macOS / Linux
source .venv/bin/activate

pip install -r requirements.txt
```

The first `pip install` downloads both SudachiPy and the `sudachidict_core` dictionary (~120 MB).

## Running

```bash
uvicorn main:app --host 0.0.0.0 --port 8000
```

Keep this server running in the background whenever you use the Japanese OCR overlay.

## API

### `POST /tokenize`

Request body:
```json
{ "text": "日本語のテキストです" }
```

Response:
```json
{
  "tokens": [
    {
      "surface": "日本語",
      "dictionary_form": "日本語",
      "reading": "ニホンゴ",
      "part_of_speech": "名詞",
      "part_of_speech_detail": "普通名詞",
      "start": 0,
      "end": 3
    },
    ...
  ]
}
```

| Field | Description |
|---|---|
| `surface` | Surface form as it appears in the input |
| `dictionary_form` | Dictionary/base form |
| `reading` | Katakana reading |
| `part_of_speech` | Main POS (品詞): 名詞, 動詞, 助詞, 補助記号, etc. |
| `part_of_speech_detail` | Sub-category (品詞細分類1): 普通名詞, 格助詞, etc. |
| `start` | Inclusive start char index into the input text |
| `end` | Exclusive end char index |

### `GET /health`

Returns `{"status": "ok"}` when ready.

## Split modes

Sudachi supports three split modes:

| Mode | Description |
|---|---|
| A | Shortest units (individual morphemes) |
| B | Intermediate compounds |
| **C** | Longest compounds (default — best for word-level overlays) |

Change `SplitMode.C` to `SplitMode.B` in `main.py` if the overlay boxes are too wide.

## C# integration

The `SudachiHttpTokenizer` service in `JapaneseOcr.App/Services/` calls this server.
Configure the URL in `%AppData%\JapaneseOcr\settings.json`:

```json
{
  "SudachiServerUrl": "http://localhost:8000"
}
```
