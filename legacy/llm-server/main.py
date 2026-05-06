"""
LLM Lookup Server — powered by Anthropic Claude

Given a single Japanese word, returns:
  - hiragana: the reading in hiragana
  - meaning:  concise English meaning

Results are cached in a local SQLite database keyed by SHA-256 of the
UTF-8-encoded word.  Each cache entry also tracks how many times the
same word has been looked up.

Endpoints:
  GET  /lookup/{word}  — look up a single Japanese word
  GET  /health         — liveness check

Start with:
  uvicorn main:app --host 127.0.0.1 --port 8100

Environment variables:
  ANTHROPIC_API_KEY   — required; your Anthropic API key
"""

import hashlib
import json
import logging
import os
import sqlite3
from contextlib import asynccontextmanager
from pathlib import Path

import anthropic
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException
from deep_translator import GoogleTranslator
import pykakasi

load_dotenv()
from pydantic import BaseModel

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

logging.basicConfig(
    level=logging.DEBUG,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    datefmt="%H:%M:%S",
)
log = logging.getLogger("llm-server")

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

DB_PATH = Path(__file__).parent / "word_cache.db"
CLAUDE_MODEL = "claude-haiku-4-5"

SYSTEM_PROMPT = (
    "You are a Japanese language expert. "
    "When given a Japanese word or phrase, respond with ONLY a JSON object "
    "containing exactly two fields:\n"
    '  "hiragana": the complete hiragana reading '
    "(if the input is already hiragana/katakana with no kanji, convert katakana "
    "to hiragana and return that)\n"
    '  "meaning": a concise English definition '
    "(1–2 short senses separated by a semicolon if needed)\n\n"
    "Respond with ONLY the JSON object — no markdown fences, no commentary.\n"
    'Example input:  日本語\n'
    'Example output: {"hiragana": "にほんご", "meaning": "Japanese language"}'
)

# ---------------------------------------------------------------------------
# Database helpers (synchronous SQLite — single-user local tool)
# ---------------------------------------------------------------------------

_db: sqlite3.Connection | None = None


def _init_db() -> sqlite3.Connection:
    conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS word_cache (
            word_hash    TEXT PRIMARY KEY,
            word         TEXT NOT NULL,
            hiragana     TEXT NOT NULL,
            meaning      TEXT NOT NULL,
            lookup_count INTEGER NOT NULL DEFAULT 1,
            created_at   TEXT NOT NULL DEFAULT (datetime('now'))
        )
    """)
    conn.commit()
    return conn


def _get_db() -> sqlite3.Connection:
    if _db is None:
        raise RuntimeError("Database not initialised")
    return _db


def _word_hash(word: str) -> str:
    """Return the hex SHA-256 digest of the UTF-8 encoded word."""
    return hashlib.sha256(word.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# Lifespan (open / close the DB connection with the app)
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app: FastAPI):
    global _db
    _db = _init_db()
    yield
    if _db:
        _db.close()
        _db = None


# ---------------------------------------------------------------------------
# FastAPI app
# ---------------------------------------------------------------------------

app = FastAPI(
    title="Japanese LLM Lookup API",
    description="Japanese word lookup (hiragana + English meaning) via Claude, with SQLite caching.",
    version="1.0.0",
    lifespan=lifespan,
)


# ---------------------------------------------------------------------------
# Request model
# ---------------------------------------------------------------------------

class LookupRequest(BaseModel):
    word: str


# ---------------------------------------------------------------------------
# Response model
# ---------------------------------------------------------------------------

class LookupResponse(BaseModel):
    hiragana: str
    meaning: str
    lookup_count: int


# ---------------------------------------------------------------------------
# Claude lookup
# ---------------------------------------------------------------------------

async def _call_claude(word: str) -> dict[str, str]:
    """Call the Claude API and parse the JSON response."""
    api_key = os.environ.get("ANTHROPIC_API_KEY", "")
    if not api_key:
        log.error("ANTHROPIC_API_KEY is not set")
        raise HTTPException(status_code=500, detail="ANTHROPIC_API_KEY is not set")

    log.debug("Calling Claude for word: %r", word)

    # Use the async client so we don't block the event loop
    client = anthropic.AsyncAnthropic(api_key=api_key)

    try:
        message = await client.messages.create(
            model=CLAUDE_MODEL,
            max_tokens=120,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": word}],
        )
    except anthropic.AuthenticationError as exc:
        log.error("Anthropic authentication failed: %s", exc)
        raise HTTPException(status_code=500, detail=f"Anthropic authentication error: {exc}")
    except anthropic.RateLimitError as exc:
        log.warning("Anthropic rate limit hit: %s", exc)
        raise HTTPException(status_code=429, detail=f"Claude rate limit: {exc}")
    except anthropic.APIStatusError as exc:
        log.error("Anthropic API error %s: %s", exc.status_code, exc.message)
        raise HTTPException(status_code=502, detail=f"Claude API error {exc.status_code}: {exc.message}")
    except Exception as exc:
        log.exception("Unexpected error calling Claude for word %r", word)
        raise HTTPException(status_code=500, detail=f"Unexpected error: {exc}")

    raw: str = message.content[0].text.strip()
    log.debug("Claude raw response for %r: %s", word, raw)

    try:
        data = json.loads(raw)
        return {
            "hiragana": str(data.get("hiragana") or word),
            "meaning":  str(data.get("meaning")  or "unknown"),
        }
    except (json.JSONDecodeError, KeyError, IndexError) as exc:
        log.warning("Failed to parse Claude response for %r — raw: %s — error: %s", word, raw, exc)
        # Graceful degradation — return raw text as meaning
        return {"hiragana": word, "meaning": raw[:200]}


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

@app.post("/lookup/claude", response_model=LookupResponse)
async def lookup_word(req: LookupRequest) -> LookupResponse:
    """
    Look up a Japanese word.

    Returns the hiragana reading and a concise English meaning.
    Results are cached; cache hits only increment the counter without
    calling Claude again.
    """
    word = req.word.strip()
    if not word:
        raise HTTPException(status_code=422, detail="'word' must not be empty")

    log.info("Lookup request: %r", word)

    db  = _get_db()
    key = _word_hash(word)

    # ── Cache hit ──────────────────────────────────────────────────────────
    try:
        row = db.execute(
            "SELECT hiragana, meaning, lookup_count FROM word_cache WHERE word_hash = ?",
            (key,),
        ).fetchone()
    except sqlite3.Error as exc:
        log.exception("DB read failed for word %r", word)
        raise HTTPException(status_code=500, detail=f"Database error: {exc}")

    if row is not None:
        hiragana, meaning, count = row
        new_count = count + 1
        try:
            db.execute(
                "UPDATE word_cache SET lookup_count = ? WHERE word_hash = ?",
                (new_count, key),
            )
            db.commit()
        except sqlite3.Error as exc:
            log.warning("DB update failed for word %r: %s", word, exc)
        log.info("Cache hit for %r (count=%d)", word, new_count)
        return LookupResponse(hiragana=hiragana, meaning=meaning, lookup_count=new_count)

    # ── Cache miss — call Claude ───────────────────────────────────────────
    log.info("Cache miss for %r — calling Claude", word)
    result = await _call_claude(word)

    try:
        db.execute(
            """
            INSERT INTO word_cache (word_hash, word, hiragana, meaning, lookup_count)
            VALUES (?, ?, ?, ?, 1)
            """,
            (key, word, result["hiragana"], result["meaning"]),
        )
        db.commit()
    except sqlite3.Error as exc:
        log.warning("DB insert failed for word %r: %s", word, exc)

    log.info("Returning result for %r: hiragana=%r meaning=%r", word, result["hiragana"], result["meaning"])
    return LookupResponse(
        hiragana=result["hiragana"],
        meaning=result["meaning"],
        lookup_count=1,
    )


@app.post("/lookup", response_model=LookupResponse)
async def lookup_word_google(req: LookupRequest) -> LookupResponse:
    """
    Look up a Japanese word using Google Translate (meaning) and pykakasi (hiragana).

    Same request/response shape as POST /lookup, but does not use Claude.
    Results are cached in the same SQLite table; cache hits skip translation.
    """
    word = req.word.strip()
    if not word:
        raise HTTPException(status_code=422, detail="'word' must not be empty")

    log.info("Google lookup request: %r", word)

    db  = _get_db()
    # Use a different key prefix so google results are cached separately
    key = "google:" + _word_hash(word)

    # ── Cache hit ──────────────────────────────────────────────────────────
    try:
        row = db.execute(
            "SELECT hiragana, meaning, lookup_count FROM word_cache WHERE word_hash = ?",
            (key,),
        ).fetchone()
    except sqlite3.Error as exc:
        log.exception("DB read failed for word %r", word)
        raise HTTPException(status_code=500, detail=f"Database error: {exc}")

    if row is not None:
        hiragana, meaning, count = row
        new_count = count + 1
        try:
            db.execute(
                "UPDATE word_cache SET lookup_count = ? WHERE word_hash = ?",
                (new_count, key),
            )
            db.commit()
        except sqlite3.Error as exc:
            log.warning("DB update failed for word %r: %s", word, exc)
        log.info("Cache hit (google) for %r (count=%d)", word, new_count)
        return LookupResponse(hiragana=hiragana, meaning=meaning, lookup_count=new_count)

    # ── Cache miss — translate + convert ──────────────────────────────────
    log.info("Cache miss (google) for %r — translating", word)

    # Hiragana via pykakasi
    kks = pykakasi.kakasi()
    result_kks = kks.convert(word)
    hiragana = "".join(item["hira"] for item in result_kks)

    # English meaning via deep-translator (Google Translate)
    try:
        meaning = GoogleTranslator(source="ja", target="en").translate(word)
    except Exception as exc:
        log.warning("Google Translate failed for %r: %s", word, exc)
        meaning = "unknown"

    try:
        db.execute(
            """
            INSERT INTO word_cache (word_hash, word, hiragana, meaning, lookup_count)
            VALUES (?, ?, ?, ?, 1)
            """,
            (key, word, hiragana, meaning),
        )
        db.commit()
    except sqlite3.Error as exc:
        log.warning("DB insert failed for word %r: %s", word, exc)

    log.info("Returning google result for %r: hiragana=%r meaning=%r", word, hiragana, meaning)
    return LookupResponse(hiragana=hiragana, meaning=meaning, lookup_count=1)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}
