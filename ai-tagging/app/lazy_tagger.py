from __future__ import annotations

import asyncio
import ctypes
import gc
import sys
import time
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from .models import ModelSpec
from .settings import Settings
from .tagger import Tagger


class LazyTagger:
    """Keeps at most one model loaded: loads on first use, swaps when another model is
    requested, and unloads after an idle period."""

    def __init__(self, settings: Settings) -> None:
        self._settings = settings
        self._tagger: Tagger | None = None
        self._condition = asyncio.Condition()
        self._in_use = 0
        self._last_used = time.monotonic()

    @property
    def loaded_model(self) -> str | None:
        return self._tagger.spec.id if self._tagger is not None else None

    @asynccontextmanager
    async def use(self, spec: ModelSpec) -> AsyncIterator[Tagger]:
        async with self._condition:
            # A swap has to wait for in-flight requests on the current model.
            await self._condition.wait_for(lambda: self._in_use == 0 or self.loaded_model == spec.id)
            if self.loaded_model != spec.id:
                if self._tagger is not None:
                    await asyncio.to_thread(self._unload)
                self._tagger = await asyncio.to_thread(Tagger.load, spec, self._settings)
            tagger = self._tagger
            self._in_use += 1

        try:
            yield tagger
        finally:
            async with self._condition:
                self._in_use -= 1
                self._last_used = time.monotonic()
                self._condition.notify_all()

    async def unload_when_idle(self) -> None:
        timeout = self._settings.idle_unload_seconds
        if timeout <= 0:
            return

        while True:
            await asyncio.sleep(min(timeout, 30))
            async with self._condition:
                if (
                    self._tagger is not None
                    and self._in_use == 0
                    and time.monotonic() - self._last_used >= timeout
                ):
                    await asyncio.to_thread(self._unload)

    def _unload(self) -> None:
        model = self.loaded_model
        self._tagger = None
        gc.collect()
        release_free_heap()
        print(f"Unloaded {model}.", file=sys.stderr, flush=True)


def release_free_heap() -> None:
    # glibc keeps freed heap pages mapped; trimming hands them back to the OS.
    if not sys.platform.startswith("linux"):
        return
    try:
        ctypes.CDLL("libc.so.6").malloc_trim(0)
    except (OSError, AttributeError):
        pass
