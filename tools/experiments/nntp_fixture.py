"""CI-ONLY test dependency for M22: a minimal localhost NNTP server, a yEnc encoder and an NZB writer.

It exists only to feed a REAL, disposable SABnzbd process with synthetic articles on 127.0.0.1 so SABnzbd performs
its own queueing, article download, decoding, assembly, post-processing and history. It is never part of the plugin,
never listens beyond localhost, and serves only articles generated from synthetic test files.

Implements exactly what SABnzbd 5.1.3 uses without credentials (sabnzbd/newswrapper.py): the 200 greeting, BODY,
ARTICLE, HEAD, STAT, DATE, CAPABILITIES, MODE READER and QUIT. Unknown message-ids answer 430 (a genuinely missing
article). A per-article delay can be set to keep a job downloading long enough to restart SABnzbd mid-download.
"""
from __future__ import annotations

import binascii
import socketserver
import threading
import time
from pathlib import Path
from xml.sax.saxutils import escape

LINE = 128


def yenc_encode(data: bytes, name: str, part: int, total: int, begin: int, file_size: int) -> bytes:
    """yEnc 1.3 multipart article body (the form SABnzbd's sabctools decoder verifies with pcrc32)."""
    out = bytearray()
    out += f"=ybegin part={part} total={total} line={LINE} size={file_size} name={name}\r\n".encode()
    out += f"=ypart begin={begin} end={begin + len(data) - 1}\r\n".encode()
    column = 0
    for i, byte in enumerate(data):
        value = (byte + 42) % 256
        escape_it = value in (0x00, 0x0A, 0x0D, 0x3D) or (value in (0x09, 0x20) and (column == 0 or i == len(data) - 1)) or (value == 0x2E and column == 0)
        if escape_it:
            out += bytes([0x3D, (value + 64) % 256])
            column += 2
        else:
            out.append(value)
            column += 1
        if column >= LINE:
            out += b"\r\n"
            column = 0
    if column:
        out += b"\r\n"
    out += f"=yend size={len(data)} part={part} pcrc32={binascii.crc32(data) & 0xFFFFFFFF:08x}\r\n".encode()
    return bytes(out)


class ArticleStore:
    def __init__(self) -> None:
        self.articles: dict[str, bytes] = {}
        self.withheld: dict[str, bytes] = {}
        self.delay = 0.0
        self.served: list[str] = []
        self.missing: list[str] = []
        self.lock = threading.Lock()

    def withhold(self, message_id: str) -> None:
        with self.lock:
            self.withheld[message_id] = self.articles.pop(message_id)

    def release_withheld(self) -> None:
        with self.lock:
            self.articles.update(self.withheld)
            self.withheld.clear()


def write_nzb(store: ArticleStore, files: list[Path], nzb: Path, job: str, segment_size: int = 120_000) -> list[str]:
    """Encode every file into articles in the store and write a standard NZB. Returns the message-ids in order."""
    ids: list[str] = []
    body = ['<?xml version="1.0" encoding="utf-8"?>', '<!DOCTYPE nzb PUBLIC "-//newzBin//DTD NZB 1.1//EN" "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd">',
            '<nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">', f'<head><meta type="title">{escape(job)}</meta></head>']
    for index, path in enumerate(files, start=1):
        data = path.read_bytes()
        parts = [data[i:i + segment_size] for i in range(0, len(data), segment_size)] or [b""]
        subject = escape(f'{job} [{index}/{len(files)}] - "{path.name}" yEnc ({len(parts)})', {"\"": "&quot;"})
        body.append(f'<file poster="crescendarr-ci@localhost" date="{int(time.time())}" subject="{subject}">')
        body.append("<groups><group>alt.binaries.crescendarr.test</group></groups><segments>")
        offset = 1
        for number, chunk in enumerate(parts, start=1):
            message_id = f"crescendarr-{abs(hash((job, path.name, number))) % 10**12}-{number}@ci.localhost"
            store.articles[message_id] = yenc_encode(chunk, path.name, number, len(parts), offset, len(data))
            offset += len(chunk)
            ids.append(message_id)
            body.append(f'<segment bytes="{len(store.articles[message_id])}" number="{number}">{escape(message_id)}</segment>')
        body.append("</segments></file>")
    body.append("</nzb>")
    nzb.write_text("\n".join(body) + "\n", encoding="utf-8")
    return ids


class NntpServer:
    def __init__(self, store: ArticleStore, port: int):
        self.store = store

        class Handler(socketserver.StreamRequestHandler):
            def handle(handler):  # noqa: N805
                handler.wfile.write(b"200 crescendarr CI-only NNTP fixture ready (posting prohibited)\r\n")
                while True:
                    line = handler.rfile.readline()
                    if not line:
                        return
                    command = line.decode("utf-8", "replace").strip()
                    verb = command.split(" ", 1)[0].upper()
                    arg = command.split(" ", 1)[1].strip() if " " in command else ""
                    if verb == "QUIT":
                        handler.wfile.write(b"205 bye\r\n")
                        return
                    if verb == "CAPABILITIES":
                        handler.wfile.write(b"101 capabilities\r\nVERSION 2\r\nREADER\r\n.\r\n")
                    elif verb == "MODE":
                        handler.wfile.write(b"200 reader\r\n")
                    elif verb == "DATE":
                        handler.wfile.write(time.strftime("111 %Y%m%d%H%M%S\r\n", time.gmtime()).encode())
                    elif verb in ("BODY", "ARTICLE", "HEAD", "STAT"):
                        message_id = arg.strip("<>")
                        with store.lock:
                            payload = store.articles.get(message_id)
                        if payload is None:
                            store.missing.append(message_id)
                            handler.wfile.write(b"430 no such article\r\n")
                            continue
                        if store.delay:
                            time.sleep(store.delay)
                        store.served.append(message_id)
                        code = {"BODY": 222, "ARTICLE": 220, "HEAD": 221, "STAT": 223}[verb]
                        handler.wfile.write(f"{code} 0 <{message_id}>\r\n".encode())
                        if verb == "STAT":
                            continue
                        if verb in ("ARTICLE", "HEAD"):
                            handler.wfile.write(f"Message-ID: <{message_id}>\r\nSubject: crescendarr test\r\n".encode())
                            handler.wfile.write(b"\r\n" if verb == "ARTICLE" else b".\r\n")
                            if verb == "HEAD":
                                continue
                        stuffed = b"\r\n".join((b"." + l if l.startswith(b".") else l) for l in payload.rstrip(b"\r\n").split(b"\r\n"))
                        handler.wfile.write(stuffed + b"\r\n.\r\n")
                    else:
                        handler.wfile.write(b"500 unknown command\r\n")

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True

        self.server = Server(("127.0.0.1", port), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()
