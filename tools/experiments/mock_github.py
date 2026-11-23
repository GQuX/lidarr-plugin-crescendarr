#!/usr/bin/env python3
"""M27, CI ONLY: a local stand-in for GitHub's releases API so stock Lidarr's REAL plugin installer can be exercised
without publishing anything.

Serves, on 127.0.0.1 only:
  https://api.github.com:443/repos/GQuX/lidarr-plugin-crescendarr/releases  -> JSON read from <releases.json> on every
      request (the test edits it to "publish" a newer candidate); TLS with a certificate for api.github.com signed by a
      throwaway CA that ONLY the disposable Lidarr container trusts.
  http://127.0.0.1:<asset-port>/assets/<name>                                  -> files from <asset-dir>
Every request is appended to <requests.log>. The container resolves api.github.com to 127.0.0.1 via --add-host; no
real GitHub endpoint, tag or release is touched.

Usage (as root, for port 443): mock_github.py <cert.pem> <key.pem> <releases.json> <asset-dir> <asset-port> <requests.log>
"""
from __future__ import annotations

import http.server
import json
import ssl
import sys
import threading
from pathlib import Path

REPO_PATH = "/repos/GQuX/lidarr-plugin-crescendarr/releases"


def main() -> int:
    cert, key, releases, assets, asset_port, requests_log = sys.argv[1:7]
    releases_path, asset_dir, log = Path(releases), Path(assets), Path(requests_log)

    def record(line: str) -> None:
        with log.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")

    class Api(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            record(f"API {self.command} {self.path} ua={self.headers.get('User-Agent')!r}")
            if self.path.split("?")[0] != REPO_PATH:
                self.send_response(404)
                self.end_headers()
                return
            body = releases_path.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):  # silence default stderr logging
            pass

    class Asset(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802
            record(f"ASSET {self.command} {self.path}")
            name = self.path.rsplit("/", 1)[-1]
            target = asset_dir / name
            if not self.path.startswith("/assets/") or not target.is_file() or "/" in name or name.startswith("."):
                self.send_response(404)
                self.end_headers()
                return
            data = target.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    api = http.server.ThreadingHTTPServer(("127.0.0.1", 443), Api)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(cert, key)
    api.socket = context.wrap_socket(api.socket, server_side=True)
    asset = http.server.ThreadingHTTPServer(("127.0.0.1", int(asset_port)), Asset)
    threading.Thread(target=asset.serve_forever, daemon=True).start()
    record("MOCK_GITHUB_READY")
    print("MOCK_GITHUB_READY", flush=True)
    api.serve_forever()
    return 0


def release(tag: str, asset_name: str, asset_port: int, published: str, body: str, draft: bool = False, target: str = "main") -> dict:
    """A GitHub API release object with exactly the fields Lidarr 3.1.6.5079's Release resource reads."""
    return {
        "url": f"https://api.github.com/repos/GQuX/lidarr-plugin-crescendarr/releases/{abs(hash(tag)) % 10**8}",
        "html_url": f"https://github.com/GQuX/lidarr-plugin-crescendarr/releases/tag/{tag}",
        "id": abs(hash(tag)) % 10**8, "node_id": "mock", "tag_name": tag, "target_commitish": target, "name": tag,
        "draft": draft, "prerelease": True, "created_at": published, "published_at": published, "body": body,
        "assets": [{"url": "mock", "id": 1, "node_id": "mock", "name": asset_name, "label": "", "content_type": "application/zip",
                    "state": "uploaded", "size": 1, "download_count": 0, "created_at": published, "updated_at": published,
                    "browser_download_url": f"http://127.0.0.1:{asset_port}/assets/{asset_name}"}],
        "tarball_url": "mock", "zipball_url": "mock",
    }


def write_releases(path: Path, releases: list[dict]) -> None:
    path.write_text(json.dumps(releases, indent=1), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
