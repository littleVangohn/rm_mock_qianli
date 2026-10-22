"""Dependency-free framed TCP transport for the Unity realtime client."""

from __future__ import annotations

import json
import select
import socket
import struct
import threading
import time
from typing import Callable


SCHEMA = "pacific-doorlock.realtime.v2"
MAX_MESSAGE_BYTES = 64 * 1024
STATE_HZ = 30.0


class ProtocolError(ValueError):
    pass


def encode_message(message: dict) -> bytes:
    body = json.dumps(message, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
    if len(body) > MAX_MESSAGE_BYTES:
        raise ProtocolError(f"message exceeds {MAX_MESSAGE_BYTES} bytes")
    return struct.pack(">I", len(body)) + body


def decode_messages(buffer: bytearray) -> list[dict]:
    messages: list[dict] = []
    while len(buffer) >= 4:
        size = struct.unpack(">I", buffer[:4])[0]
        if size == 0 or size > MAX_MESSAGE_BYTES:
            raise ProtocolError(f"invalid message size: {size}")
        if len(buffer) < size + 4:
            break
        body = bytes(buffer[4:4 + size])
        del buffer[:4 + size]
        try:
            message = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ProtocolError(f"invalid JSON: {error}") from error
        if not isinstance(message, dict):
            raise ProtocolError("message root must be an object")
        messages.append(message)
    return messages


class RealtimeTCPServer:
    """Single-active-client transport; a new Unity Play session replaces the old one."""

    def __init__(self, host: str, port: int, simulation: object) -> None:
        self.host = host
        self.port = port
        self.simulation = simulation
        self.stop_event: threading.Event = simulation.stop_event
        self.listener: socket.socket | None = None
        self.listener_thread: threading.Thread | None = None
        self.active_lock = threading.Lock()
        self.active_socket: socket.socket | None = None
        self.state_sequence = 0

    def start(self) -> threading.Thread:
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((self.host, self.port))
        self.port = int(listener.getsockname()[1])
        listener.listen(4)
        listener.settimeout(0.2)
        self.listener = listener
        self.listener_thread = threading.Thread(target=self._accept_loop, name="unity-tcp-listener", daemon=True)
        self.listener_thread.start()
        return self.listener_thread

    def stop(self) -> None:
        listener, self.listener = self.listener, None
        if listener is not None:
            self._close_socket(listener)
        with self.active_lock:
            active, self.active_socket = self.active_socket, None
        if active is not None:
            self._close_socket(active)

    @staticmethod
    def _close_socket(sock: socket.socket) -> None:
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        try:
            sock.close()
        except OSError:
            pass

    def _accept_loop(self) -> None:
        while not self.stop_event.is_set():
            listener = self.listener
            if listener is None:
                return
            try:
                client, _address = listener.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            client.settimeout(0.02)
            with self.active_lock:
                previous = self.active_socket
                self.active_socket = client
            if previous is not None:
                self._close_socket(previous)
            threading.Thread(target=self._client_loop, args=(client,), name="unity-tcp-client", daemon=True).start()

    def _is_active(self, client: socket.socket) -> bool:
        with self.active_lock:
            return self.active_socket is client

    @staticmethod
    def _send(client: socket.socket, message: dict) -> None:
        client.sendall(encode_message(message))

    def _state_message(self) -> dict:
        # The simulation thread publishes this snapshot at 30 Hz. Building it
        # here used to contend with the simulation lock and could stall the
        # stream for hundreds of milliseconds during projectile flight.
        payload = self.simulation.latest_state()
        if payload is None:
            payload = self.simulation.state()
        self.state_sequence += 1
        return {
            "type": "state", "schema": SCHEMA, "seq": self.state_sequence,
            "sentAt": time.monotonic(), "payload": payload,
        }

    def _client_loop(self, client: socket.socket) -> None:
        buffer = bytearray()
        next_state = time.perf_counter()
        last_control_sequence = -1
        try:
            while not self.stop_event.is_set() and self._is_active(client):
                # Wait for input, but never past the next state deadline. The
                # old loop blocked in recv(2 cm) and could delay a state
                # packet by up to that timeout, which showed up in Unity as
                # uneven motion even though the simulation ran at 500 Hz.
                wait = max(0.0, min(next_state - time.perf_counter(), 0.05))
                try:
                    readable, _, _ = select.select([client], [], [], wait)
                    if readable:
                        chunk = client.recv(16 * 1024)
                        if not chunk:
                            return
                        buffer.extend(chunk)
                    # Drain anything else already queued before building the
                    # state so fresh controls are reflected immediately.
                    while select.select([client], [], [], 0)[0]:
                        chunk = client.recv(16 * 1024)
                        if not chunk:
                            return
                        buffer.extend(chunk)
                except (OSError, ValueError):
                    return
                for message in decode_messages(buffer):
                    sequence = int(message.get("seq", -1))
                    message_type = message.get("type")
                    if message_type in {"control", "heartbeat"}:
                        if sequence <= last_control_sequence:
                            continue
                        last_control_sequence = sequence
                        payload = message.get("payload") or {}
                        if not isinstance(payload, dict):
                            raise ProtocolError("control payload must be an object")
                        self.simulation.update_control(payload)
                    elif message_type == "command":
                        self._handle_command(client, sequence, message)
                    else:
                        self._send(client, {
                            "type": "error", "schema": SCHEMA, "seq": sequence,
                            "error": f"unsupported message type: {message_type}",
                        })
                now = time.perf_counter()
                if now >= next_state:
                    started = time.perf_counter()
                    self._send(client, self._state_message())
                    # Schedule from when this packet started, so steady-state
                    # cadence is exactly 30 Hz, but never leave the deadline in
                    # the past after a slow send (that caused back-to-back
                    # packets and a visible gap afterwards).
                    next_state = started + 1.0 / STATE_HZ
                    if next_state <= time.perf_counter():
                        next_state = time.perf_counter() + 1.0 / STATE_HZ
        except (ConnectionError, OSError):
            pass
        except (ProtocolError, TypeError, ValueError) as error:
            try:
                self._send(client, {"type": "error", "schema": SCHEMA, "seq": -1, "error": str(error)})
            except OSError:
                pass
        finally:
            with self.active_lock:
                if self.active_socket is client:
                    self.active_socket = None
            self._close_socket(client)

    def _handle_command(self, client: socket.socket, sequence: int, message: dict) -> None:
        name = str(message.get("name", ""))
        payload = message.get("payload") or {}
        if not isinstance(payload, dict):
            raise ProtocolError("command payload must be an object")
        handlers: dict[str, Callable[[dict], dict]] = {
            "fire": lambda _payload: self.simulation.fire(),
            "reset": lambda _payload: self.simulation.reset(),
            "calibration": self.simulation.set_calibration,
            "modules": self.simulation.set_modules,
            "marker": self.simulation.set_marker,
            "mode": self.simulation.set_control_mode,
            "ammo": self.simulation.set_ammo,
            "select_robot": self.simulation.select_robot,
        }
        handler = handlers.get(name)
        if handler is None:
            result = {"ok": False, "error": f"unsupported command: {name}"}
        else:
            try:
                result = handler(payload)
            except (TypeError, ValueError) as error:
                result = {"ok": False, "error": str(error)}
        self._send(client, {
            "type": "ack", "schema": SCHEMA, "seq": sequence, "name": name,
            "ok": bool(result.get("ok", True)), "payload": result,
            "error": result.get("error") or result.get("reason"),
        })
