from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, unquote, urlparse
import html
import mimetypes
import sqlite3
import sys


BASE_DIR = Path(__file__).resolve().parent
DB_PATH = BASE_DIR / "picscan.db"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg"}


def connect_db():
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn


def init_db():
    with connect_db() as conn:
        conn.execute(
            """
            CREATE TABLE IF NOT EXISTS images (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_no TEXT NOT NULL UNIQUE,
                filename TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                data BLOB NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """
        )


def import_images(image_dir):
    image_dir = Path(image_dir)
    if not image_dir.exists():
        raise FileNotFoundError(f"画像フォルダが見つかりません: {image_dir}")

    imported = 0
    with connect_db() as conn:
        for path in image_dir.iterdir():
            if not path.is_file() or path.suffix.lower() not in IMAGE_EXTENSIONS:
                continue

            model_no = path.name
            mime_type = mimetypes.guess_type(path.name)[0] or "application/octet-stream"
            conn.execute(
                """
                INSERT INTO images (model_no, filename, mime_type, data)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(model_no) DO UPDATE SET
                    filename = excluded.filename,
                    mime_type = excluded.mime_type,
                    data = excluded.data
                """,
                (model_no, path.name, mime_type, path.read_bytes()),
            )
            imported += 1

    return imported


def find_image(model_no):
    normalized = model_no.strip()
    if not normalized:
        return None

    with connect_db() as conn:
        return conn.execute(
            """
            SELECT model_no, filename, mime_type, data
            FROM images
            WHERE model_no = ? OR filename = ?
            LIMIT 1
            """,
            (normalized, normalized),
        ).fetchone()


def render_page(model_no="", found=None, not_found=False):
    escaped_model = html.escape(model_no, quote=True)
    image_markup = ""

    if found:
        encoded_model = quote(found["model_no"])
        escaped_filename = html.escape(found["filename"])
        image_markup = f"""
            <section class="result">
                <div class="resultHeader">
                    <div>
                        <p class="label">検索結果</p>
                        <h2>{escaped_filename}</h2>
                    </div>
                </div>
                <div class="imageFrame">
                    <img src="/image/{encoded_model}" alt="{escaped_filename}">
                </div>
            </section>
        """
    elif not_found:
        image_markup = """
            <section class="emptyState" role="status">
                <p class="label">検索結果</p>
                <h2>該当する画像が見つかりません</h2>
                <p>入力した型番と同じ名前の画像ファイルがデータベースに登録されているか確認してください。</p>
            </section>
        """
    else:
        image_markup = """
            <section class="emptyState">
                <p class="label">PicScan</p>
                <h2>型番を入力すると、登録済み画像を表示します</h2>
                <p>例: <code>ABC-123.png</code></p>
            </section>
        """

    return f"""<!doctype html>
<html lang="ja">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>PicScan</title>
    <link rel="stylesheet" href="/static/styles.css">
</head>
<body>
    <main class="appShell">
        <header class="topBar">
            <div>
                <p class="label">画像検索</p>
                <h1>PicScan</h1>
            </div>
        </header>

        <form class="searchPanel" method="get" action="/">
            <label for="model">型番</label>
            <div class="searchRow">
                <input id="model" name="model" value="{escaped_model}" placeholder="型番または画像ファイル名" autocomplete="off" autofocus>
                <button type="submit">検索</button>
            </div>
        </form>

        {image_markup}
    </main>
</body>
</html>"""


class AppHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)

        if parsed.path == "/":
            params = parse_qs(parsed.query)
            model_no = params.get("model", [""])[0]
            found = find_image(model_no) if model_no.strip() else None
            body = render_page(model_no=model_no, found=found, not_found=bool(model_no.strip() and not found))
            self.send_html(body)
            return

        if parsed.path.startswith("/image/"):
            model_no = unquote(parsed.path.removeprefix("/image/"))
            found = find_image(model_no)
            if not found:
                self.send_error(HTTPStatus.NOT_FOUND, "Image not found")
                return

            data = found["data"]
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", found["mime_type"])
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "private, max-age=300")
            self.end_headers()
            self.wfile.write(data)
            return

        if parsed.path == "/static/styles.css":
            self.send_css()
            return

        self.send_error(HTTPStatus.NOT_FOUND)

    def log_message(self, fmt, *args):
        sys.stdout.write("%s - %s\n" % (self.address_string(), fmt % args))

    def send_html(self, body):
        encoded = body.encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def send_css(self):
        body = (BASE_DIR / "static" / "styles.css").read_bytes()
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "text/css; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def run_server(host="127.0.0.1", port=8000):
    init_db()
    server = ThreadingHTTPServer((host, port), AppHandler)
    print(f"PicScan is running: http://{host}:{port}")
    server.serve_forever()


def main():
    init_db()
    if len(sys.argv) >= 3 and sys.argv[1] == "import":
        count = import_images(sys.argv[2])
        print(f"{count}件の画像をデータベースに取り込みました。")
        return

    host = "127.0.0.1"
    port = int(sys.argv[1]) if len(sys.argv) >= 2 else 8000
    run_server(host, port)


if __name__ == "__main__":
    main()
