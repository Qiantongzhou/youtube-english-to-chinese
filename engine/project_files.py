"""Locate media belonging to an opened project, including folders moved since export."""
from pathlib import Path


def relocate_media(project, folder):
    folder = Path(folder).resolve()
    # Extracted audio is always stored at the project root, unlike imported video.
    previous = Path(project["audio"]).parent if project.get("audio") else None
    defaults = {"video": "source.mp4", "audio": "original.wav", "dub_audio": "dub.wav",
                "background": "stems/htdemucs/original/no_vocals.wav",
                "result": "中文配音.mp4", "content_file": "视频标题与内容简介.txt"}
    for field, fallback in defaults.items():
        value = project.get(field)
        if not value: continue
        stored = Path(value)
        # Relinking in the editor is an explicit choice; do not replace a valid
        # absolute video path with an older source.mp4 that happens to be nearby.
        if field == "video" and stored.is_absolute() and stored.is_file():
            project[field] = str(stored.resolve())
            continue
        candidates = []
        if not stored.is_absolute(): candidates.append(folder / stored)
        if previous and stored.is_absolute():
            try: candidates.append(folder / stored.relative_to(previous))
            except ValueError: pass
        candidates.extend([folder / stored.name, folder / fallback, stored])
        for candidate in candidates:
            if candidate.is_file():
                project[field] = str(candidate.resolve())
                break
    return project
