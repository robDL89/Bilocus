# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for the pure codec. Does NOT import bpy: runs under pytest with the system Python.
import io
import os
import socket
import sys
import threading

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_protocol as protocol


def reader_from_bytes(data):
    """Implements the read_exactly contract: b"" if the stream is already
    finished at the first byte, EOFError if it finishes mid-read."""
    stream = io.BytesIO(data)

    def read_exactly(count):
        if count == 0:
            return b""
        chunk = stream.read(count)
        if not chunk:
            return b""
        if len(chunk) < count:
            raise EOFError("stream closed after {} of {} bytes".format(len(chunk), count))
        return chunk

    return read_exactly


def dripping_reader(data):
    # returns one byte at a time: reproduces TCP's partial reads
    state = {"pos": 0}

    def read_exactly(count):
        out = bytearray()
        while len(out) < count:
            if state["pos"] >= len(data):
                if not out:
                    return b""
                raise EOFError("stream exhausted")
            out.append(data[state["pos"]])
            state["pos"] += 1
        return bytes(out)

    return read_exactly


# --- round trip on decode_message (parsed header) -------------------------

def test_round_trip_header_and_payload():
    raw = protocol.encode_frame({"type": "hello"}, b"\x01\x02\x03")
    header, payload = protocol.decode_message(reader_from_bytes(raw))
    assert header == {"type": "hello"}
    assert payload == b"\x01\x02\x03"


def test_round_trip_empty_payload():
    raw = protocol.encode_frame({"type": "clear"})
    header, payload = protocol.decode_message(reader_from_bytes(raw))
    assert header == {"type": "clear"}
    assert payload == b""


def test_decode_handles_partial_reads():
    raw = protocol.encode_frame({"type": "geometry"}, b"\x09\x08\x07")
    header, payload = protocol.decode_message(dripping_reader(raw))
    assert header == {"type": "geometry"}
    assert payload == b"\x09\x08\x07"


def test_two_frames_in_sequence():
    raw = protocol.encode_frame({"n": 1}, b"\x01") + protocol.encode_frame({"n": 2}, b"\x02\x02")
    read_exactly = reader_from_bytes(raw)
    first_header, first_payload = protocol.decode_message(read_exactly)
    second_header, second_payload = protocol.decode_message(read_exactly)
    assert first_header == {"n": 1}
    assert first_payload == b"\x01"
    assert second_header == {"n": 2}
    assert second_payload == b"\x02\x02"


# --- decode_frame returns the raw string, like the C# side -----------------

def test_decode_frame_returns_raw_header_string():
    raw = protocol.encode_frame_raw('{"type":"hello"}', b"\x01\x02")
    header_text, payload = protocol.decode_frame(reader_from_bytes(raw))
    assert header_text == '{"type":"hello"}'
    assert isinstance(header_text, str)
    assert payload == b"\x01\x02"


def test_decode_frame_does_not_validate_json():
    # M5: framing intact, header not JSON. decode_frame must not notice,
    # exactly like FrameCodec.Read on the C# side.
    raw = protocol.encode_frame_raw("{non json")
    header_text, payload = protocol.decode_frame(reader_from_bytes(raw))
    assert header_text == "{non json"
    assert payload == b""


def test_decode_frame_accepts_empty_header():
    raw = protocol.encode_frame_raw("")
    header_text, payload = protocol.decode_frame(reader_from_bytes(raw))
    assert header_text == ""
    assert payload == b""


def test_decode_frame_utf8_four_byte_header():
    # M3: the Python side never decoded a header with 4-byte UTF-8.
    text = '{"n":"\U0001F600"}'
    raw = protocol.encode_frame_raw(text)
    header_text, payload = protocol.decode_frame(reader_from_bytes(raw))
    assert header_text == text
    assert payload == b""


def test_decode_frame_invalid_utf8_header_raises():
    raw = b"\x02\x00\x00\x00" + b"\xc3\x28" + b"\x00\x00\x00\x00"
    with pytest.raises(protocol.BridgeFramingError):
        protocol.decode_frame(reader_from_bytes(raw))


def test_decode_frame_invalid_utf8_header_not_caught_as_message_error():
    # The trap: UnicodeDecodeError is a subclass of ValueError, so an
    # except ValueError written for the recoverable case (malformed JSON)
    # would catch it by mistake. With the project's own types, an invalid
    # UTF-8 header is BridgeFramingError and must NOT be caught by an
    # except BridgeMessageError: it is terminal, not recoverable.
    raw = b"\x02\x00\x00\x00" + b"\xc3\x28" + b"\x00\x00\x00\x00"
    try:
        protocol.decode_frame(reader_from_bytes(raw))
        assert False, "should have raised BridgeFramingError"
    except protocol.BridgeMessageError:
        assert False, "a framing error must not be caught as a message error"
    except protocol.BridgeFramingError:
        pass


def test_decode_message_malformed_json_not_caught_as_framing_error():
    # The symmetric case: malformed JSON with intact framing is
    # BridgeMessageError and must NOT be caught by an except
    # BridgeFramingError: the stream is still aligned, it can continue.
    raw = protocol.encode_frame_raw("{non json")
    try:
        protocol.decode_message(reader_from_bytes(raw))
        assert False, "should have raised BridgeMessageError"
    except protocol.BridgeFramingError:
        assert False, "a message error must not be caught as a framing error"
    except protocol.BridgeMessageError:
        pass


# --- decode_message: malformed JSON is recoverable ------------------------

def test_decode_message_malformed_json_raises_value_error():
    raw = protocol.encode_frame_raw("{non json")
    with pytest.raises(protocol.BridgeMessageError):
        protocol.decode_message(reader_from_bytes(raw))


def test_decode_message_empty_header_raises_value_error():
    raw = protocol.encode_frame_raw("")
    with pytest.raises(protocol.BridgeMessageError):
        protocol.decode_message(reader_from_bytes(raw))


def test_decode_message_keeps_stream_aligned_after_bad_json():
    # the point of Correction 2: intact framing means the next frame can
    # still be read, the connection must not be closed
    raw = protocol.encode_frame_raw("{non json", b"\x01") + protocol.encode_frame({"n": 2})
    read_exactly = reader_from_bytes(raw)
    with pytest.raises(protocol.BridgeMessageError):
        protocol.decode_message(read_exactly)
    header, payload = protocol.decode_message(read_exactly)
    assert header == {"n": 2}
    assert payload == b""


# --- encoding: the limits also apply on write --------------------------

def test_encode_raw_little_endian_prefixes():
    raw = protocol.encode_frame_raw("ab", b"\x07")
    assert raw == b"\x02\x00\x00\x00ab\x01\x00\x00\x00\x07"


def test_encode_header_over_limit_raises():
    oversized = "x" * (protocol.MAX_HEADER_BYTES + 1)
    with pytest.raises(protocol.BridgeFramingError) as error:
        protocol.encode_frame_raw(oversized)
    assert "header" in str(error.value)


def test_encode_payload_over_limit_raises():
    oversized = b"\x00" * (protocol.MAX_PAYLOAD_BYTES + 1)
    with pytest.raises(protocol.BridgeFramingError) as error:
        protocol.encode_frame_raw("{}", oversized)
    assert "payload" in str(error.value)


# --- decoding: limits, truncation, clean close -------------------------

def test_header_over_limit_raises():
    bogus = protocol._U32.pack(protocol.MAX_HEADER_BYTES + 1)
    with pytest.raises(protocol.BridgeFramingError):
        protocol.decode_frame(reader_from_bytes(bogus))


def test_payload_over_limit_raises():
    # M4: the branch that desynchronizes the stream, previously uncovered
    bogus = protocol.encode_frame_raw("{}")[:-4] + protocol._U32.pack(protocol.MAX_PAYLOAD_BYTES + 1)
    with pytest.raises(protocol.BridgeFramingError):
        protocol.decode_frame(reader_from_bytes(bogus))


def test_truncated_stream_raises():
    raw = protocol.encode_frame({"type": "hello"}, b"\x01\x02\x03")
    with pytest.raises(EOFError):
        protocol.decode_frame(reader_from_bytes(raw[:-2]))


def test_empty_stream_returns_none():
    assert protocol.decode_frame(reader_from_bytes(b"")) is None
    assert protocol.decode_message(reader_from_bytes(b"")) is None


def test_frame_then_clean_close_returns_none():
    raw = protocol.encode_frame({"n": 1}, b"\x01")
    read_exactly = reader_from_bytes(raw)
    assert protocol.decode_message(read_exactly) is not None
    assert protocol.decode_message(read_exactly) is None


def test_truncated_length_prefix_raises():
    with pytest.raises(EOFError):
        protocol.decode_frame(reader_from_bytes(b"\x01\x00"))


def test_short_callback_raises_eof_not_struct_error():
    # a broken callback used to produce struct.error, obscure and far from the cause
    def broken(count):
        return b"\x01\x02"

    with pytest.raises(EOFError):
        protocol.decode_frame(broken)


# --- make_socket_reader on real sockets --------------------------------------

def test_socket_reader_normal_read():
    left, right = socket.socketpair()
    try:
        left.sendall(b"abcxyz")
        read_exactly = protocol.make_socket_reader(right)
        assert read_exactly(3) == b"abc"
        assert read_exactly(3) == b"xyz"
    finally:
        left.close()
        right.close()


def test_socket_reader_split_across_chunks():
    left, right = socket.socketpair()
    try:
        def writer():
            for chunk in [b"ab", b"cx", b"yz"]:
                left.sendall(chunk)

        thread = threading.Thread(target=writer)
        thread.start()
        read_exactly = protocol.make_socket_reader(right)
        assert read_exactly(6) == b"abcxyz"
        thread.join()
    finally:
        left.close()
        right.close()


def test_socket_reader_clean_close_returns_empty():
    left, right = socket.socketpair()
    try:
        left.close()
        read_exactly = protocol.make_socket_reader(right)
        assert read_exactly(4) == b""
    finally:
        right.close()


def test_socket_reader_close_mid_read_raises():
    left, right = socket.socketpair()
    try:
        left.sendall(b"ab")
        left.close()
        read_exactly = protocol.make_socket_reader(right)
        with pytest.raises(EOFError):
            read_exactly(4)
    finally:
        right.close()


def test_socket_reader_decodes_a_real_frame():
    left, right = socket.socketpair()
    try:
        left.sendall(protocol.encode_frame({"type": "hello"}, b"\x0a\x0b"))
        left.close()
        read_exactly = protocol.make_socket_reader(right)
        header, payload = protocol.decode_message(read_exactly)
        assert header == {"type": "hello"}
        assert payload == b"\x0a\x0b"
        # after the frame the peer closed at the boundary: clean close
        assert protocol.decode_message(read_exactly) is None
    finally:
        right.close()


def test_constants_match_design():
    assert protocol.DEFAULT_PORT == 9877
    assert protocol.PROTOCOL_VERSION == 1
    assert protocol.METERS_PER_FOOT == 0.3048
