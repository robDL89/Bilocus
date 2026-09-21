# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

import binascii
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_protocol as protocol

VECTORS = os.path.join(os.path.dirname(__file__), "..", "vectors", "frames.json")


def load_vectors():
    with open(VECTORS, "r", encoding="utf-8") as handle:
        return json.load(handle)


DATA = load_vectors()


def test_vector_file_is_not_empty():
    # without this, an empty list would make the parametrized test pass vacuously
    count = len(DATA["vectors"])
    assert count >= 5, "expected at least 5 golden vectors, found {}".format(count)


@pytest.mark.parametrize("vector", DATA["vectors"], ids=[v["name"] for v in DATA["vectors"]])
def test_encoded_frame_matches_golden_vector(vector):
    payload = binascii.unhexlify(vector["payload_hex"])
    produced = protocol.encode_frame_raw(vector["header"], payload)
    assert binascii.hexlify(produced).decode("ascii") == vector["frame_hex"]


def test_constants_match_shared_contract():
    # the constants are as much a wire contract as the framing: if someone
    # changes them on one side only, the other side's tests must fail
    constants = DATA["constants"]
    assert protocol.PROTOCOL_VERSION == constants["protocol_version"]
    assert protocol.DEFAULT_PORT == constants["default_port"]
    assert protocol.DEFAULT_HOST == constants["default_host"]
    assert protocol.METERS_PER_FOOT == constants["meters_per_foot"]
    assert protocol.MAX_HEADER_BYTES == constants["max_header_bytes"]
    assert protocol.MAX_PAYLOAD_BYTES == constants["max_payload_bytes"]
