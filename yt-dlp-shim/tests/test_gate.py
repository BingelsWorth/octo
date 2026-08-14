"""The gate is what stops background prewarm from starving a user pressing play,
so the properties worth pinning are the ones whose failure is invisible.

A leaked permit does not throw: the gate simply stops bounding anything (or
decays toward deadlock) and the box falls over later under load, far from the
edit that caused it. Likewise a doubled timeout budget just looks like "the shim
is slow sometimes". Both are asserted here directly.
"""
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from gate import TieredGate  # noqa: E402


def test_reserved_slots_stay_reachable_when_background_is_saturated():
    """The whole point: background at capacity must not block a play."""
    gate = TieredGate(total=5, reserve=2)

    for _ in range(gate.total - gate.reserve):
        assert gate.acquire(bg=True, timeout=1)

    # Background is now at its ceiling and must be refused...
    assert not gate.acquire(bg=True, timeout=0.2)
    # ...while the reserved slots remain available to interactive work.
    assert gate.acquire(bg=False, timeout=0.2)
    assert gate.acquire(bg=False, timeout=0.2)


def test_failed_background_acquire_does_not_leak_a_permit():
    """A mismatched release is the classic two-semaphore bug and is silent.

    Fill the gate with interactive holders so a background acquire clears the
    outer semaphore and then fails on the main one. If the outer permit is not
    handed back, background capacity shrinks permanently.
    """
    gate = TieredGate(total=3, reserve=1)
    bg_capacity = gate.total - gate.reserve

    for _ in range(gate.total):
        assert gate.acquire(bg=False, timeout=1)

    # Clears _bg, cannot get _main, must return the _bg permit on the way out.
    assert not gate.acquire(bg=True, timeout=0.2)

    for _ in range(gate.total):
        gate.release(bg=False)

    # Full background capacity must still be there.
    for _ in range(bg_capacity):
        assert gate.acquire(bg=True, timeout=0.5), "background permit was leaked"
    assert not gate.acquire(bg=True, timeout=0.2)


def test_total_capacity_is_still_bounded_after_churn():
    """Releasing must not inflate the gate into bounding nothing."""
    gate = TieredGate(total=3, reserve=1)

    for _ in range(4):
        assert gate.acquire(bg=True, timeout=1)
        gate.release(bg=True)

    for _ in range(gate.total):
        assert gate.acquire(bg=False, timeout=0.5)
    assert not gate.acquire(bg=False, timeout=0.2), "gate stopped bounding"


def test_background_acquire_honours_one_shared_deadline():
    """Two per-semaphore timeouts would let background wait twice as long as the
    caller's own HTTP timeout, holding a worker thread for a dead response."""
    gate = TieredGate(total=2, reserve=1)
    for _ in range(gate.total):
        assert gate.acquire(bg=False, timeout=1)

    timeout = 0.4
    started = time.monotonic()
    assert not gate.acquire(bg=True, timeout=timeout)
    elapsed = time.monotonic() - started

    assert elapsed < timeout * 1.8, f"waited {elapsed:.2f}s against a {timeout}s budget"


def test_reserve_is_clamped_so_background_never_collapses():
    """A config typo must degrade background, not deadlock it."""
    gate = TieredGate(total=5, reserve=9)

    assert gate.total - gate.reserve >= 1
    assert gate.acquire(bg=True, timeout=0.5)


def test_interactive_waits_are_served_once_a_slot_frees():
    """Sanity check that the gate blocks rather than busy-fails under contention."""
    gate = TieredGate(total=1, reserve=0)
    assert gate.acquire(bg=False, timeout=1)

    acquired = []

    def waiter():
        acquired.append(gate.acquire(bg=False, timeout=2))

    t = threading.Thread(target=waiter)
    t.start()
    time.sleep(0.1)
    gate.release(bg=False)
    t.join(timeout=3)

    assert acquired == [True]
