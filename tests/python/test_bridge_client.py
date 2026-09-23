# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_client.py, the socket client side (no bpy import: runs
# under pytest with the system Python, like test_protocol.py).
#
# The fake server uses port 0 (the OS assigns a free one): 9877 is occupied
# by Revit during this work session and must not be touched.

import os
import socket
import struct
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_client
import bridge_protocol as protocol


class FakeServer(object):
    """Minimal TCP server for the tests. Uses the real codec (bridge_protocol)
    to read and write frames, so the tests exercise the same contract that
    BridgeServer would use on the Revit side."""

    def __init__(self):
        self._listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._listener.bind((protocol.DEFAULT_HOST, 0))
        self._listener.listen(1)
        self.port = self._listener.getsockname()[1]
        self._conns = []

    def accept_one(self, timeout=5.0):
        self._listener.settimeout(timeout)
        conn, _ = self._listener.accept()
        conn.settimeout(timeout)
        self._conns.append(conn)
        return conn

    def close(self):
        for conn in self._conns:
            try:
                conn.close()
            except Exception:
                pass
        try:
            self._listener.close()
        except Exception:
            pass


def read_one_message(conn, timeout=5.0):
    conn.settimeout(timeout)
    read_exactly = protocol.make_socket_reader(conn)
    return protocol.decode_message(read_exactly)


def wait_for(predicate, timeout=5.0, interval=0.02):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return predicate()


def test_connect_sends_hello_handshake():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        header, payload = read_one_message(conn)

        assert header["type"] == "hello"
        assert header["protocol_version"] == protocol.PROTOCOL_VERSION
        assert header["client"] == "blender"
        assert payload == b""
        assert client_obj.running is True
        assert client_obj.status.startswith("connected")
    finally:
        client_obj.disconnect()
        server.close()


def test_tcp_nodelay_is_enabled_on_client_socket():
    # binding rule of the task: this is the side that sends transforms at 30 Hz
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # drena l'hello

        nodelay = client_obj.socket.getsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY)
        assert nodelay != 0
    finally:
        client_obj.disconnect()
        server.close()


def test_receive_frames_in_sequence():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        conn.sendall(protocol.encode_frame({
            "type": "hello_ack", "protocol_version": 1,
            "revit_version": "2025", "doc_title": "Test Project"}))
        conn.sendall(protocol.encode_frame({"type": "revit_batch_begin", "count": 2}))
        conn.sendall(protocol.encode_frame({"type": "revit_batch_end"}))

        assert wait_for(lambda: client_obj.incoming.qsize() >= 3)

        first = client_obj.incoming.get(timeout=1)
        second = client_obj.incoming.get(timeout=1)
        third = client_obj.incoming.get(timeout=1)

        assert first[0]["type"] == "hello_ack"
        assert second[0]["type"] == "revit_batch_begin"
        assert third[0]["type"] == "revit_batch_end"
    finally:
        client_obj.disconnect()
        server.close()


def test_clean_server_close_stops_thread_without_error():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        # clean close at a frame boundary: not an error, see DESIGN.md 5.1
        conn.shutdown(socket.SHUT_RDWR)
        conn.close()

        assert wait_for(lambda: client_obj.running is False)
        assert "closed by Revit" in client_obj.status
    finally:
        client_obj.disconnect()
        server.close()


def test_server_close_leaves_no_socket_open_on_client_side():
    # scenario observed in the field: Blender connected, then Revit gets
    # closed while Blender stays open. netstat showed a persistent
    # CLOSE_WAIT on the Blender side: the read loop noticed the disconnect
    # but never closed its own socket. Here the fake server closes ONLY its
    # own end and stays put: nobody calls disconnect() on the client side,
    # it must close itself.
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        raw_socket = client_obj.socket

        conn.shutdown(socket.SHUT_RDWR)
        conn.close()

        assert wait_for(lambda: client_obj.running is False)
        # robust check: the attribute becomes None AND the actual socket
        # (captured before the attribute was cleared) turns out closed.
        # netstat is not used, only the client is queried
        assert wait_for(lambda: client_obj.socket is None)
        assert raw_socket.fileno() == -1
    finally:
        client_obj.disconnect()
        server.close()


def test_reconnect_after_disconnect():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn1 = server.accept_one()
        header1, _ = read_one_message(conn1)
        assert header1["type"] == "hello"

        client_obj.disconnect()
        assert client_obj.running is False
        # the status of a voluntary disconnect must not be overwritten by
        # the read thread noticing the close in a race: see the note in the
        # "message is None" branch of _read_loop
        assert client_obj.status == "disconnected"

        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn2 = server.accept_one()
        header2, _ = read_one_message(conn2)

        assert header2["type"] == "hello"
        assert client_obj.running is True
    finally:
        client_obj.disconnect()
        server.close()


def test_late_old_read_thread_does_not_kill_new_connection():
    # Disconnect and an immediate Connect: the old read thread can wake up
    # from its closed socket AFTER the new connection is up. Simulated
    # deterministically by running the old loop by hand once the new
    # connection exists: it must leave the new one alone.
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn1 = server.accept_one()
        read_one_message(conn1)  # hello
        old_socket = client_obj.socket
        old_thread = client_obj.thread

        client_obj.disconnect()
        old_thread.join(timeout=5.0)

        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn2 = server.accept_one()
        read_one_message(conn2)  # hello
        new_socket = client_obj.socket
        status = client_obj.status

        client_obj._read_loop(old_socket)

        assert client_obj.running is True
        assert client_obj.socket is new_socket
        assert new_socket.fileno() != -1
        assert client_obj.status == status

        # and the new connection still works both ways
        assert client_obj.send({"type": "sync_end"}) is True
        header, _ = read_one_message(conn2)
        assert header["type"] == "sync_end"
    finally:
        client_obj.disconnect()
        server.close()


def test_malformed_json_is_recoverable_stream_stays_aligned():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        # valid framing, header body is not JSON: BridgeMessageError,
        # recoverable. The stream stays aligned: the next frame must still
        # arrive
        conn.sendall(protocol.encode_frame_raw("this is not json"))
        conn.sendall(protocol.encode_frame({"type": "revit_batch_end"}))

        assert wait_for(lambda: client_obj.incoming.qsize() >= 1)
        assert client_obj.running is True
        assert "message ignored" in client_obj.status

        header, _ = client_obj.incoming.get(timeout=1)
        assert header["type"] == "revit_batch_end"
    finally:
        client_obj.disconnect()
        server.close()


def test_framing_error_is_terminal_and_closes_connection():
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        # header not valid UTF-8: BridgeFramingError, terminal. The stream
        # is desynchronized, the only correct response is to close
        u32 = struct.Struct("<I")
        bad_header = b"\xff\xfe\xfd\xfc"
        conn.sendall(u32.pack(len(bad_header)) + bad_header + u32.pack(0))

        assert wait_for(lambda: client_obj.running is False)
        assert "disconnected" in client_obj.status
    finally:
        client_obj.disconnect()
        server.close()


def test_connect_to_unreachable_port_reports_failure_without_raising():
    # free port but nobody listening: the connection refusal must be
    # reported in the status, it must not propagate an exception to the
    # panel's operator
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind((protocol.DEFAULT_HOST, 0))
    port = listener.getsockname()[1]
    listener.close()

    client_obj = bridge_client.BridgeClient()
    client_obj.connect(host=protocol.DEFAULT_HOST, port=port)

    assert client_obj.running is False
    assert client_obj.socket is None
    assert "connection failed" in client_obj.status


def test_high_volume_frames_arrive_in_order_via_queue():
    # demonstrates that the only channel between the read thread and the
    # rest of the addon is the queue: no message is lost or mixed up under
    # load, which would be the symptom of shared state touched without
    # synchronization
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        total = 200
        for index in range(total):
            conn.sendall(protocol.encode_frame({"type": "transform", "obj_id": "obj-{}".format(index)}))

        assert wait_for(lambda: client_obj.incoming.qsize() >= total, timeout=10.0)

        for index in range(total):
            header, _ = client_obj.incoming.get(timeout=1)
            assert header["obj_id"] == "obj-{}".format(index)
    finally:
        client_obj.disconnect()
        server.close()


def test_send_discards_oversized_frame_without_touching_socket():
    # header too large: encode_frame rejects BEFORE writing to the stream.
    # Recoverable, see DESIGN.md 5.1 table of write outcomes: the frame is
    # discarded, the connection stays intact
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        huge = {"type": "geometry", "junk": "x" * (protocol.MAX_HEADER_BYTES + 1024)}
        result = client_obj.send(huge)

        assert result is False
        assert "frame discarded" in client_obj.status
        assert client_obj.running is True
        assert client_obj.socket is not None

        # the connection is still alive and usable
        ok = client_obj.send({"type": "sync_end"})
        assert ok is True
        header, _ = read_one_message(conn)
        assert header["type"] == "sync_end"
    finally:
        client_obj.disconnect()
        server.close()


def test_send_failure_mid_frame_closes_connection():
    # write interrupted mid-frame: terminal, the socket must be closed
    # (not just recorded in the status). See DESIGN.md 5.1
    server = FakeServer()
    client_obj = bridge_client.BridgeClient()
    try:
        client_obj.connect(host=protocol.DEFAULT_HOST, port=server.port)
        conn = server.accept_one()
        read_one_message(conn)  # hello

        client_obj.socket.shutdown(socket.SHUT_WR)
        result = client_obj.send({"type": "transform", "obj_id": "x"})

        assert result is False
        assert "write error" in client_obj.status
        assert client_obj.running is False
        assert client_obj.socket is None
    finally:
        client_obj.disconnect()
        server.close()


def test_bridge_client_module_never_imports_bpy():
    # binding rule of the task: Blender's API is not thread-safe and this
    # module runs on the socket thread. Static check on the source, not
    # just on the test environment (which has no bpy anyway)
    path = os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon", "bridge_client.py")
    with open(path, "r") as handle:
        source = handle.read()
    assert "bpy" not in source
