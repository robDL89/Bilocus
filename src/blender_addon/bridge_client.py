# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Client socket toward Revit.
# Blender's API is NOT thread-safe: this module never touches it.
# Incoming messages land in a queue that the addon's timer drains on the
# main thread.

import socket
import threading

try:
    import queue
except ImportError:
    import Queue as queue

import bridge_protocol as protocol


class BridgeClient(object):

    def __init__(self):
        self.socket = None
        self.thread = None
        self.running = False
        self.status = "disconnected"
        self.incoming = queue.Queue()
        self._write_lock = threading.Lock()

    def connect(self, host=None, port=None):
        if self.running:
            return
        host = host if host else protocol.DEFAULT_HOST
        port = port if port else protocol.DEFAULT_PORT

        sock = None
        try:
            sock = socket.create_connection((host, port), timeout=5.0)
            sock.settimeout(None)
            # same reason as the NoDelay on the Revit side (BridgeServer). If
            # anything it matters more here: the 84-byte transform frames at
            # 30 Hz start from THIS side, and it is on this socket that
            # Nagle plus delayed ACK would do visible damage.
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        except Exception as error:
            # any trouble at connect time (refused, timeout, host not
            # resolvable): this is not one of the classified outcomes of the
            # receive loop, it is just "we couldn't"
            self.status = "connection failed: {}".format(error)
            if sock is not None:
                try:
                    sock.close()
                except Exception:
                    pass
            self.socket = None
            return

        self.socket = sock
        self.running = True
        self.status = "connected to {}:{}".format(host, port)

        # The thread gets ITS socket: see _owns for why it must not read
        # self.socket instead.
        self.thread = threading.Thread(target=self._read_loop, args=(sock,))
        self.thread.daemon = True
        self.thread.start()

        self.send({"type": "hello",
                   "protocol_version": protocol.PROTOCOL_VERSION,
                   "client": "blender"})

    def disconnect(self):
        self.running = False
        self._force_close()
        self.status = "disconnected"

    def _owns(self, sock):
        """True while `sock` is still THE connection of this client.

        A read thread only touches the shared state (status, running,
        socket) while this holds. After Disconnect and an immediate
        Connect, the old thread wakes up late from its closed socket: if it
        looked at self.running it would see the NEW connection's True, mark
        it dead and close the new socket, and the bridge would drop without
        anyone having asked."""
        return self.running and self.socket is sock

    def _force_close(self, sock=None):
        # does not touch self.status: the reason for closing depends on the
        # caller (voluntary disconnect, or a write interrupted mid-frame in
        # send), so the caller writes the status
        if sock is None:
            sock = self.socket
        if sock is None:
            return
        try:
            # shutdown before close: it is what cleanly unblocks a recv()
            # blocked on the read thread on POSIX. On Windows the blocked
            # thread gets an OSError regardless (see the comment in the
            # "except OSError" branch of _read_loop), but shutdown is still
            # correct: it is the standard way to signal closure to the TCP
            # peer
            sock.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass
        try:
            sock.close()
        except Exception:
            pass
        # only the connection being closed is forgotten: a newer one opened
        # in the meantime stays where it is
        if self.socket is sock:
            self.socket = None

    def send(self, header, payload=None):
        sock = self.socket
        if sock is None:
            return False

        try:
            raw = protocol.encode_frame(header, payload)
        except protocol.BridgeFramingError as error:
            # frame rejected before touching the stream (header or payload
            # over the limits): not a single byte went out, the connection
            # stays intact. Recoverable, the frame is discarded. See
            # DESIGN.md 5.1, the table of write outcomes
            self.status = "frame discarded: {}".format(error)
            return False

        with self._write_lock:
            try:
                sock.sendall(raw)
                return True
            except Exception as error:
                # write interrupted mid-frame: the peer has already read a
                # length prefix and will never receive the promised bytes,
                # it is desynchronized exactly as on the read side. Terminal:
                # the socket must be closed, it is not enough to record the
                # error in the status and carry on as if nothing happened.
                # See DESIGN.md 5.1, the table of write outcomes
                if self.socket is sock:
                    self.status = "write error: {}".format(error)
                    self.running = False
                self._force_close(sock)
                return False

    def _read_loop(self, sock):
        # make_socket_reader implements the read_exactly contract required
        # by the codec: b"" if the peer closes before sending any bytes,
        # EOFError if it closes mid-read. That distinction is what lets
        # decode_message return None on a clean disconnect.
        read_exactly = protocol.make_socket_reader(sock)
        while self._owns(sock):
            try:
                message = protocol.decode_message(read_exactly)
            except protocol.BridgeMessageError as error:
                # malformed JSON with intact framing: the stream is still
                # aligned, the connection can continue. The distinction with
                # the framing error (below) is in the types, no more need
                # for an except ValueError that would risk also catching
                # UnicodeDecodeError: it is a subclass of ValueError, but it
                # comes from decode_frame and is TERMINAL, not recoverable.
                if self._owns(sock):
                    self.status = "message ignored: {}".format(error)
                continue
            except (protocol.BridgeFramingError, EOFError) as error:
                # length error, invalid UTF-8 or truncation: the stream is
                # lost, the only correct response is to close
                if self._owns(sock):
                    self.status = "disconnected: {}".format(error)
                    self.running = False
                break
            except OSError:
                # our own disconnect() closed the socket while this thread
                # was blocked in recv(). On POSIX a concurrent
                # shutdown(SHUT_RDWR) makes recv() return with 0 bytes,
                # which decode_frame reads as a clean close (None, below).
                # On Windows instead Winsock raises OSError (WinError 10038,
                # "an operation was attempted on something that is not a
                # socket") as soon as the file descriptor is closed: it is
                # not one of the four outcomes in DESIGN.md 5.1, it is a
                # platform detail below the framing level. If it was not us
                # (the socket is still ours) it is still an unexpected
                # network failure: it must be treated as terminal the same
                # way.
                if self._owns(sock):
                    self.status = "disconnected: connection interrupted"
                    self.running = False
                break

            if message is None:
                # clean close at a frame boundary. If it was Revit that
                # closed, the socket is still ours and it is correct to say
                # so in the status. If instead it was US who called
                # disconnect() (the status is already "disconnected"), do
                # not overwrite it: "connection closed by Revit" after a
                # voluntary disconnect would be misleading.
                if self._owns(sock):
                    self.status = "connection closed by Revit"
                    self.running = False
                break

            # a message read just as the connection was being replaced
            # belongs to the old one: it does not reach the queue
            if not self._owns(sock):
                break
            self.incoming.put(message)

        # the loop exits only on a terminal outcome (clean close,
        # BridgeFramingError/EOFError, OSError) or because the socket is no
        # longer ours: in each of these cases THIS socket must be closed
        # here, otherwise it stays open on the Blender side even after the
        # peer is gone (seen in the field: CLOSE_WAIT after Revit closes).
        # _force_close() is idempotent and only forgets self.socket if it is
        # still this one.
        self._force_close(sock)


CLIENT = BridgeClient()
