from __future__ import annotations

import asyncio
import ctypes
import gc
import sys
import time
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from .settings import Settings
from .tagger import CamieTagger


class LazyTagger:
    """Loads the model on first use and unloads it after an idle period."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._tagger: CamieTagger | None = None
        self._lock = asyncio.Lock()
        self._in_use = 0
        self._last_used = time.monotonic()

    @property
    def loaded(self) -> bool:
        return self._tagger is not None

    @asynccontextmanager
    async def use(self) -> AsyncIterator[CamieTagger]:
        async with self._lock:
            if self._tagger is None:
                self._tagger = await asyncio.to_thread(CamieTagger.load, self._settings)
            self._in_use += 1

        try:
            yield self._tagger
        finally:
            self._in_use -= 1
            self._last_used = time.monotonic()

    async def unload_when_idle(self) -> None:
        timeout = self._settings.idle_unload_seconds
        if timeout <= 0:
            return

        while True:
            await asyncio.sleep(min(timeout, 30))
            async with self._lock:
                if (
                    self._tagger is not None
                    and self._in_use == 0
                    and time.monotonic() - self._last_used >= timeout
                ):
                    await asyncio.to_thread(self._unload)

    def _unload(self) -> None:
        self._tagger = None
        gc.collect()
        release_free_heap()
        print("Unloaded idle model.", file=sys.stderr, flush=True)


def release_free_heap() -> None:
    # glibc keeps freed heap pages mapped; trimming hands them back to the OS.
    if not sys.platform.startswith("linux"):
        return
    try:
        ctypes.CDLL("libc.so.6").malloc_trim(0)
    except (OSError, AttributeError):
        pass
