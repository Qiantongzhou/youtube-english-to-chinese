"""Select the voice model independently of recognition and translation quality."""

PRESET_TTS_SIZES = {"quick": "0.6B", "quality": "1.7B", "polished": "1.7B"}


def model_identity(settings):
    """Return the actual engine identity; online voices bypass local settings."""
    voice = settings.get("voice", "Serena")
    if isinstance(voice, str) and voice.startswith("edge:"):
        return voice
    selected = settings.get("voice_model", "auto")
    if selected not in ("auto", "0.6B", "1.7B"):
        raise ValueError("配音模型设置无效，请选择跟随制作档位、Qwen 0.6B 或 Qwen 1.7B。")
    if selected == "auto":
        mode = settings.get("mode", "quality")
        if mode not in PRESET_TTS_SIZES:
            raise ValueError("制作档位设置无效，请重新选择配音模型或制作档位。")
        selected = PRESET_TTS_SIZES[mode]
    return f"Qwen/Qwen3-TTS-12Hz-{selected}-CustomVoice"
