"""
Japanese Tokenizer API — powered by SudachiPy

Endpoints:
  POST /tokenize   — tokenize a Japanese string
  GET  /health     — liveness check

Start with:
  uvicorn main:app --host 0.0.0.0 --port 8000
"""

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import sudachipy
import sudachipy.dictionary

app = FastAPI(
    title="Japanese Tokenizer API",
    description="Sudachi-based morphological analysis for Japanese text.",
    version="1.0.0",
)

# ---------------------------------------------------------------------------
# Sudachi initialisation
# ---------------------------------------------------------------------------
# SudachiPy uses a split mode:
#   A — shortest units (characters / morphemes)
#   B — intermediate units
#   C — longest units (whole compound words)
# Mode C gives the most natural "word" level segmentation, which is what the
# overlay needs. Change to SplitMode.B if C produces overly long compounds.

_tokenizer_obj: sudachipy.Tokenizer | None = None


def _get_tokenizer() -> sudachipy.Tokenizer:
    global _tokenizer_obj
    if _tokenizer_obj is None:
        dic = sudachipy.Dictionary()
        _tokenizer_obj = dic.create(mode=sudachipy.SplitMode.C)
    return _tokenizer_obj


# ---------------------------------------------------------------------------
# Request / response models
# ---------------------------------------------------------------------------

class TokenizeRequest(BaseModel):
    text: str


class TokenDto(BaseModel):
    surface: str
    dictionary_form: str
    reading: str
    part_of_speech: str         # main POS category, e.g. 名詞, 動詞, 助詞
    part_of_speech_detail: str  # first sub-category, e.g. 普通名詞, 格助詞
    start: int                  # inclusive start char index into the input text
    end: int                    # exclusive end char index


class TokenizeResponse(BaseModel):
    tokens: list[TokenDto]


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _build_token(morpheme: sudachipy.Morpheme) -> TokenDto:
    pos = morpheme.part_of_speech()  # tuple of up to 6 strings
    pos_main   = pos[0] if len(pos) > 0 else ""
    pos_detail = pos[1] if len(pos) > 1 and pos[1] not in ("", "*") else ""

    try:
        reading = morpheme.reading_form()
    except Exception:
        reading = ""

    try:
        dict_form = morpheme.dictionary_form()
    except Exception:
        dict_form = morpheme.surface()

    return TokenDto(
        surface=morpheme.surface(),
        dictionary_form=dict_form,
        reading=reading,
        part_of_speech=pos_main,
        part_of_speech_detail=pos_detail,
        start=morpheme.begin(),
        end=morpheme.end(),
    )


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------

@app.post("/tokenize", response_model=TokenizeResponse)
def tokenize(request: TokenizeRequest) -> TokenizeResponse:
    """Tokenize the given Japanese text and return morpheme-level tokens."""
    if not request.text:
        return TokenizeResponse(tokens=[])

    try:
        tokenizer = _get_tokenizer()
        morphemes = tokenizer.tokenize(request.text)
    except Exception as exc:
        raise HTTPException(status_code=500, detail=f"Tokenization failed: {exc}") from exc

    tokens = [_build_token(m) for m in morphemes]
    return TokenizeResponse(tokens=tokens)


@app.get("/health")
def health() -> dict:
    """Liveness probe — returns 200 when the server is ready."""
    # Trigger lazy init so the first real request is fast
    try:
        _get_tokenizer()
        return {"status": "ok"}
    except Exception as exc:
        raise HTTPException(status_code=503, detail=str(exc)) from exc
