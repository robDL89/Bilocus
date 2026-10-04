# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Bilocus protocol codec.
# This module must NOT import bpy: it is shared with the tests and with the addon.
# Frame format, little-endian:
#   [uint32 header length][UTF-8 header][uint32 payload length][payload]

import json
import struct

PROTOCOL_VERSION = 2
DEFAULT_HOST = "127.0.0.1"

# 9876 is taken by blender-mcp: do not reuse it.
DEFAULT_PORT = 9877

METERS_PER_FOOT = 0.3048
FEET_PER_METER = 1.0 / METERS_PER_FOOT

MAX_HEADER_BYTES = 1048576
MAX_PAYLOAD_BYTES = 268435456

_U32 = struct.Struct("<I")


class BridgeError(Exception):
    """Common root of the protocol errors."""


class BridgeFramingError(BridgeError):
    """The stream is desynchronized for good: the only correct response is to
    close the socket. Length over the limit, invalid header UTF-8, violated
    read_exactly contract."""


class BridgeMessageError(BridgeError):
    """Framing is intact and the stream is still aligned: it is fine to
    respond with an error and continue. Malformed JSON header."""


def encode_frame_raw(header_text, payload=None):
    """Builds a frame from an already-serialized header string.

    Lengths are validated here, before producing bytes: encoder and decoder
    must agree on what a valid frame is. A frame over the limit written
    silently would only be rejected on arrival, and by that point the
    receiver's stream is already lost (see the comment on decode_frame).
    Raises BridgeFramingError if header or payload exceed the limits.
    """
    header_bytes = header_text.encode("utf-8")
    if len(header_bytes) > MAX_HEADER_BYTES:
        raise BridgeFramingError("header of {} bytes over the limit of {} bytes, excess of {} bytes".format(
            len(header_bytes), MAX_HEADER_BYTES, len(header_bytes) - MAX_HEADER_BYTES))

    body = payload if payload else b""
    if len(body) > MAX_PAYLOAD_BYTES:
        raise BridgeFramingError("payload of {} bytes over the limit of {} bytes, excess of {} bytes".format(
            len(body), MAX_PAYLOAD_BYTES, len(body) - MAX_PAYLOAD_BYTES))

    return b"".join([
        _U32.pack(len(header_bytes)),
        header_bytes,
        _U32.pack(len(body)),
        body,
    ])


def encode_frame(header, payload=None):
    """Builds a frame from a header dict."""
    text = json.dumps(header, separators=(",", ":"))
    return encode_frame_raw(text, payload)


def decode_frame(read_exactly):
    """Reads a frame and returns (header_text, payload).

    The header comes back as an UNPARSED STRING, exactly like the C# side
    does: JSON validation is a layer above, see decode_message.

    Contract of the callback: read_exactly(n) must return exactly n bytes;
    it must return b"" if the peer closed BEFORE sending any byte, and raise
    EOFError if it closed mid-read. This is the only information that tells
    a clean disconnect apart from a truncated frame. make_socket_reader
    implements this contract on a real socket.

    Return contract and error contract, for whoever builds the receive loop:

    - returns None when the peer closed EXACTLY at a frame boundary, i.e. the
      very first read of the header length prefix returns zero bytes. This is
      the clean disconnect, the expected case: Blender can be restarted
      multiple times during a Revit session. It is not an error.

    - raises EOFError if the close happens mid-frame.

    - raises BridgeFramingError on a length over the limits, and also on a
      header that is not valid UTF-8 (the decoder re-raises the original
      UnicodeDecodeError there as BridgeFramingError). BOTH ARE TERMINAL FOR
      THE CONNECTION. When the payload length is rejected, the payload bytes
      are still sitting in the socket: the stream is desynchronized for good
      and there is no way to realign it, because the protocol has no
      delimiters. The only correct response is to close the socket. An
      except/log/continue would read garbage forever.
    """
    header_length = _read_length(read_exactly, MAX_HEADER_BYTES, "header", allow_eof=True)
    if header_length is None:
        return None
    header_bytes = _read_body(read_exactly, header_length, "header")
    payload_length = _read_length(read_exactly, MAX_PAYLOAD_BYTES, "payload")
    payload = _read_body(read_exactly, payload_length, "payload")
    try:
        header_text = header_bytes.decode("utf-8")
    except UnicodeDecodeError as error:
        raise BridgeFramingError(
            "header is not valid UTF-8, the connection must be closed: {}".format(error)) from error
    return header_text, payload


def decode_message(read_exactly):
    """Reads a frame and parses its header as JSON.
    Raises BridgeMessageError if the JSON is malformed: in that case the
    stream is still aligned and the connection can continue, unlike framing
    errors (BridgeFramingError) which are terminal.
    Returns None on a clean close at a frame boundary, like decode_frame."""
    frame = decode_frame(read_exactly)
    if frame is None:
        return None
    header_text, payload = frame
    try:
        header = json.loads(header_text)
    except ValueError as error:
        raise BridgeMessageError("malformed JSON header: {}".format(error)) from error
    return header, payload


def make_socket_reader(sock):
    """Returns a read_exactly(n) that reads from the given socket.
    Returns b"" if the peer closes before sending any byte, raises EOFError
    if it closes mid-read."""

    def read_exactly(count):
        if count <= 0:
            return b""
        chunks = []
        received = 0
        while received < count:
            chunk = sock.recv(count - received)
            if not chunk:
                if received == 0:
                    # clean close: none of this read's bytes had arrived
                    return b""
                raise EOFError("connection closed after {} of {} bytes".format(received, count))
            chunks.append(chunk)
            received += len(chunk)
        return b"".join(chunks)

    return read_exactly


def _read_length(read_exactly, maximum, what, allow_eof=False):
    # the byte count must be checked by hand: a broken callback that returns
    # fewer than 4 bytes would otherwise produce an obscure struct.error far
    # from the actual cause
    raw = read_exactly(4)
    if allow_eof and not raw:
        return None
    if len(raw) != 4:
        raise EOFError("{} length prefix incomplete: {} of 4 bytes".format(what, len(raw)))
    value = _U32.unpack(raw)[0]
    if value > maximum:
        raise BridgeFramingError("{} length of {} over the limit of {}".format(what, value, maximum))
    return value


def _read_body(read_exactly, length, what):
    if length == 0:
        return b""
    data = read_exactly(length)
    if len(data) != length:
        raise EOFError("{} body incomplete: {} of {} bytes".format(what, len(data), length))
    return data
