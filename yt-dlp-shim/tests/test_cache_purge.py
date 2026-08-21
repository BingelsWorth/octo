"""The 403 self-heal purges yt-dlp's cache. It must survive the cache root
being a mount point, which is how compose runs it."""
import os
import sys

import pytest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

app_module = pytest.importorskip("app")


def test_purges_contents_but_keeps_the_directory(tmp_path, monkeypatch):
    cache = tmp_path / "yt-dlp"
    (cache / "nested").mkdir(parents=True)
    (cache / "nested" / "player.js").write_text("stale")
    (cache / "loose.json").write_text("stale")

    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    app_module._purge_cache_contents()

    # The directory itself survives: removing it would be EBUSY under a mount.
    assert cache.is_dir()
    assert list(cache.iterdir()) == []


def test_is_a_no_op_when_there_is_no_cache(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_CACHE_HOME", str(tmp_path))
    app_module._purge_cache_contents()  # must not raise
