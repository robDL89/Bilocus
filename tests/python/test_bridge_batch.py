# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_receive.BatchState, the bookkeeping of a pull from Revit.
# Does NOT import bpy: runs under pytest with the system Python.
#
# The state lives here and not in the module that touches bpy for a
# precise reason: a batch can stay OPEN. Revit can fail partway through
# sending, or the connection can drop between a revit_geometry and the
# revit_batch_end. What happens in that case is a decision, and a decision
# must be tested.

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_receive


def make_state():
    return bridge_receive.BatchState()


# --- initial state -----------------------------------------------------------

def test_starts_closed_with_a_readable_message():
    state = make_state()
    assert not state.open
    assert state.received == 0
    assert state.message == bridge_receive.NO_PULL_MESSAGE


# --- complete batch -------------------------------------------------------------

def test_a_complete_batch_counts_created_and_updated():
    state = make_state()
    state.begin(3, 100.0)
    state.record(True, 100.1)
    state.record(False, 100.2)
    state.record(True, 100.3)
    state.end(100.4)

    assert not state.open
    assert state.received == 3
    assert state.created == 2
    assert state.updated == 1
    assert "3" in state.message


def test_the_message_while_open_shows_the_progress_against_the_expected_count():
    state = make_state()
    state.begin(12, 100.0)
    state.record(True, 100.1)
    assert "1" in state.message
    assert "12" in state.message


def test_a_second_batch_does_not_keep_the_counters_of_the_first():
    state = make_state()
    state.begin(2, 100.0)
    state.record(True, 100.1)
    state.record(True, 100.2)
    state.end(100.3)

    state.begin(1, 200.0)
    assert state.received == 0
    assert state.created == 0
    state.record(False, 200.1)
    state.end(200.2)
    assert state.received == 1
    assert state.created == 0
    assert state.updated == 1


# --- geometry without revit_batch_begin --------------------------------------------
#
# Decision: the geometry is imported anyway, opening an implicit batch with
# an unknown expected count. Losing a wall because a header without a
# payload was missed would be the wrong trade-off.

def test_geometry_without_a_begin_opens_an_implicit_batch():
    state = make_state()
    state.record(True, 100.0)
    assert state.open
    assert state.received == 1
    assert state.expected is None


def test_an_implicit_batch_closes_normally_on_batch_end():
    state = make_state()
    state.record(True, 100.0)
    assert state.end(100.1)
    assert not state.open
    assert state.received == 1


def test_batch_end_without_anything_open_changes_nothing():
    state = make_state()
    assert not state.end(100.0)
    assert state.message == bridge_receive.NO_PULL_MESSAGE


# --- batch left open ----------------------------------------------------------

def test_a_new_begin_closes_the_previous_batch_and_says_so():
    state = make_state()
    state.begin(5, 100.0)
    state.record(True, 100.1)

    note = state.begin(2, 200.0)
    assert note is not None
    # the new batch starts clean despite the previous one's interruption
    assert state.open
    assert state.received == 0
    assert state.expected == 2


def test_a_first_begin_reports_no_interruption():
    state = make_state()
    assert state.begin(5, 100.0) is None


def test_a_dropped_connection_closes_an_open_batch():
    state = make_state()
    state.begin(5, 100.0)
    state.record(True, 100.1)

    reason = state.check_interrupted(False, 100.2)
    assert reason is not None
    assert not state.open
    # the two objects that already arrived stay counted: they are in the scene
    assert state.received == 1
    assert "1" in state.message


def test_an_open_batch_survives_while_the_connection_is_alive():
    state = make_state()
    state.begin(5, 100.0)
    state.record(True, 100.1)
    assert state.check_interrupted(True, 100.2) is None
    assert state.open


def test_a_stalled_batch_times_out_even_with_the_connection_alive():
    # Revit can die without the socket dropping right away: without this
    # check the panel would stay forever on "pull in progress"
    state = make_state()
    state.begin(5, 100.0)
    state.record(True, 100.1)
    late = 100.1 + bridge_receive.BATCH_TIMEOUT_SECONDS + 1.0
    assert state.check_interrupted(True, late) is not None
    assert not state.open


def test_check_interrupted_does_nothing_when_no_batch_is_open():
    state = make_state()
    assert state.check_interrupted(False, 100.0) is None
    assert state.message == bridge_receive.NO_PULL_MESSAGE


def test_check_interrupted_is_idempotent():
    state = make_state()
    state.begin(5, 100.0)
    state.record(True, 100.1)
    first = state.check_interrupted(False, 100.2)
    message_after_first = state.message
    assert state.check_interrupted(False, 100.3) is None
    assert state.message == message_after_first
    assert first is not None


# --- failed elements ----------------------------------------------------------------

def test_a_failed_element_is_counted_apart_from_the_imported_ones():
    state = make_state()
    state.begin(2, 100.0)
    state.record(True, 100.1)
    state.record_failure(100.2)
    state.end(100.3)
    assert state.received == 1
    assert state.failed == 1
    assert "1" in state.message


def test_a_failure_without_an_open_batch_opens_an_implicit_one():
    state = make_state()
    state.record_failure(100.0)
    assert state.open
    assert state.failed == 1


# --- the message is always ASCII and always a string ---------------------------------

def test_every_message_is_a_plain_ascii_string():
    state = make_state()
    messages = [state.message]
    state.begin(2, 100.0)
    messages.append(state.message)
    state.record(True, 100.1)
    messages.append(state.message)
    state.record_failure(100.2)
    messages.append(state.message)
    state.end(100.3)
    messages.append(state.message)
    state.begin(1, 200.0)
    state.record(False, 200.1)
    state.check_interrupted(False, 200.2)
    messages.append(state.message)

    for message in messages:
        assert isinstance(message, str)
        assert message
        message.encode("ascii")


# --- shared meshes of the batch ----------------------------------------------

def test_stored_meshes_live_until_the_batch_ends():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    assert state.meshes["k"] == ((0.0,) * 9, (0, 1, 2))
    state.end(100.2)
    assert state.meshes == {}


def test_an_interrupted_batch_drops_its_meshes():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    assert state.check_interrupted(False, 100.2) is not None
    assert state.meshes == {}


def test_a_new_begin_drops_the_previous_meshes():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    state.begin(1, 100.2)
    assert state.meshes == {}


def test_a_mesh_without_begin_opens_an_implicit_batch_and_survives():
    state = make_state()
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.0)
    assert state.open
    state.record(True, 100.1)
    assert "k" in state.meshes


def test_overwritten_edits_are_reported_in_the_summary():
    state = make_state()
    state.begin(2, 100.0)
    state.record(False, 100.1, overwritten=True)
    state.record(False, 100.2)
    state.end(100.3)
    assert state.overwritten == 1
    assert "1 edited mesh overwritten" in state.message


def test_no_overwrite_no_mention():
    state = make_state()
    state.begin(1, 100.0)
    state.record(True, 100.1)
    state.end(100.2)
    assert "overwritten" not in state.message
