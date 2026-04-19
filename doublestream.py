"""
YOLO WHEP Streaming Server — Multi-Source
------------------------------------------
Each camera gets its own viewer and WHEP endpoint:

    /0/          → viewer for camera 0
    /0/whep      → WHEP endpoint for camera 0
    /1/          → viewer for camera 1
    /1/whep      → WHEP endpoint for camera 1
    ...

    /detection   → SSE stream of per-camera person detection state
                   e.g. "01" = cam 0 empty, cam 1 has person

SOURCE_MAP below defines the index → device mapping.

Requirements:
    pip install aiortc aiohttp av ultralytics opencv-python

Usage:
    python yolo_whep_server.py --port 8080 --cert cert.pem --key key.pem

Connect from browser:
    https://10.10.9.81:8080/0/      (camera 0)
    https://10.10.9.81:8080/1/      (camera 1)
    https://10.10.9.81:8080/detection
"""

import argparse
import asyncio
import fractions
import json
import logging
import ssl
import uuid
from threading import Thread

import cv2
import numpy as np
from aiohttp import web
from aiortc import MediaStreamTrack, RTCPeerConnection, RTCSessionDescription
from av import VideoFrame
from ultralytics import YOLO

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("yolo-whep")

# ── Source map: URL index → camera device ─────────────────────────────────────
SOURCE_MAP: dict[str, int | str] = {
    "1": 0,
    "0": 1,
}

PERSON_CLASS = 0

pcs: dict[str, RTCPeerConnection] = {}


# ── YOLO frame buffer ──────────────────────────────────────────────────────────

class YOLOFrameBuffer:
    def __init__(self, source: int | str, conf_thresh: float):
        self.source = source
        self.conf_thresh = conf_thresh
        self._frame = None
        self._person_count = 0          # tracks latest detection count
        self._running = False
        self.model = YOLO("yolov8n.pt")

    def start(self):
        self._running = True
        Thread(target=self._run, daemon=True).start()
        logger.info("YOLO inference started (source=%s)", self.source)

    def stop(self):
        self._running = False

    def latest_frame(self):
        return self._frame

    def person_detected(self) -> bool:
        """True if the most recent frame contained at least one person."""
        return self._person_count >= 1

    def _run(self):
        for r in self.model(
            self.source,
            classes=[PERSON_CLASS],
            conf=self.conf_thresh,
            stream=True,
            verbose=False,
        ):
            if not self._running:
                break
            self._person_count = len(r.boxes)   # update before drawing
            frame = r.orig_img.copy()
            for box in r.boxes:
                x1, y1, x2, y2 = map(int, box.xyxy[0])
                conf = float(box.conf[0])
                cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 220, 90), 2)
                cv2.putText(
                    frame, f"person {conf:.2f}", (x1, y1 - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 220, 90), 2,
                )
            cv2.putText(
                frame, f"People: {len(r.boxes)}", (12, 32),
                cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 255), 2,
            )
            self._frame = frame


# ── Video track ────────────────────────────────────────────────────────────────

class YOLOVideoTrack(MediaStreamTrack):
    kind = "video"

    def __init__(self, buffer: YOLOFrameBuffer, fps: int = 30):
        super().__init__()
        self._buffer = buffer
        self._fps = fps
        self._pts = 0
        self._time_base = fractions.Fraction(1, fps)

    async def recv(self):
        await asyncio.sleep(1 / self._fps)
        bgr = self._buffer.latest_frame()
        if bgr is None:
            bgr = np.zeros((480, 640, 3), dtype=np.uint8)
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        av_frame = VideoFrame.from_ndarray(rgb, format="rgb24")
        av_frame.pts = self._pts
        av_frame.time_base = self._time_base
        self._pts += 1
        return av_frame


# ── CORS headers ───────────────────────────────────────────────────────────────

CORS = {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "POST, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type, Authorization",
}


# ── Helpers ────────────────────────────────────────────────────────────────────

def get_buffer(request) -> YOLOFrameBuffer | None:
    cam_idx = request.match_info["cam_idx"]
    return request.app["buffers"].get(cam_idx)


# ── WHEP handlers ──────────────────────────────────────────────────────────────

async def whep_options(request):
    cam_idx = request.match_info["cam_idx"]
    return web.Response(
        status=200,
        headers={
            **CORS,
            "Access-Control-Expose-Headers": "Link, Location, ETag",
            "Link": f'</{cam_idx}/whep>; rel="urn:ietf:params:whep:ext:core:server-sent-events"',
        },
    )


async def whep_post(request):
    cam_idx = request.match_info["cam_idx"]
    buf = get_buffer(request)
    if buf is None:
        return web.Response(status=404, text=f"Camera index '{cam_idx}' not found")

    body = await request.text()
    if body.strip().startswith("{"):
        data = json.loads(body)
        offer_sdp = RTCSessionDescription(sdp=data["sdp"], type=data["type"])
    else:
        offer_sdp = RTCSessionDescription(sdp=body, type="offer")

    session_id = str(uuid.uuid4())
    pc = RTCPeerConnection()
    pcs[session_id] = pc

    @pc.on("connectionstatechange")
    async def on_state():
        logger.info("[%s cam=%s] state → %s", session_id[:8], cam_idx, pc.connectionState)
        if pc.connectionState in ("failed", "closed"):
            await pc.close()
            pcs.pop(session_id, None)

    fps: int = request.app["fps"]
    pc.addTrack(YOLOVideoTrack(buf, fps))

    await pc.setRemoteDescription(offer_sdp)
    answer = await pc.createAnswer()
    await pc.setLocalDescription(answer)

    logger.info("[%s cam=%s] WHEP session created", session_id[:8], cam_idx)

    return web.Response(
        status=201,
        content_type="application/sdp",
        headers={
            **CORS,
            "Location": f"/{cam_idx}/whep/{session_id}",
            "ETag": f'"{session_id}"',
        },
        text=pc.localDescription.sdp,
    )


async def whep_delete(request):
    session_id = request.match_info["session_id"]
    pc = pcs.pop(session_id, None)
    if pc:
        await pc.close()
        logger.info("[%s] WHEP session deleted", session_id[:8])
        return web.Response(status=200, headers=CORS)
    return web.Response(status=404, headers=CORS)


# ── Detection stream endpoint ──────────────────────────────────────────────────

async def detection_stream(request):
    """
    GET /detection
    Server-Sent Events stream. Each event payload is one character per camera
    (sorted by key), e.g. "01" means cam "0" has no person, cam "1" does.
    Events are only emitted when the state changes.
    """
    response = web.StreamResponse(
        headers={
            **CORS,
            "Content-Type": "text/event-stream",
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",
        }
    )
    await response.prepare(request)

    buffers: dict[str, YOLOFrameBuffer] = request.app["buffers"]
    fps: int = request.app["fps"]
    ordered_keys = sorted(buffers.keys())
    last_state: str | None = None

    try:
        while True:
            state = "".join(
                "1" if buffers[k].person_detected() else "0"
                for k in ordered_keys
            )
            if state != last_state:
                for _ in range(5): await response.write(f"data: {state}\n\n".encode())
                last_state = state
            await asyncio.sleep(1 / fps)
    except (asyncio.CancelledError, ConnectionResetError):
        pass

    return response


# ── Per-camera viewer HTML ─────────────────────────────────────────────────────

def make_viewer_html(cam_idx: str, source: int | str) -> str:
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>YOLO · Camera {cam_idx} (source={source})</title>
<style>
  * {{ margin: 0; padding: 0; box-sizing: border-box; }}
  body {{ background: #000; display: flex; flex-direction: column;
         align-items: center; justify-content: center;
         min-height: 100vh; font-family: monospace; color: #0f0; }}
  video {{ width: 100vw; max-width: 1280px; aspect-ratio: 16/9;
          display: block; background: #000; }}
  #status {{ padding: 8px 16px; font-size: 13px; letter-spacing: .1em; }}
  #label  {{ padding: 4px 16px; font-size: 11px; color: #0a0; letter-spacing: .08em; }}
  button {{ margin: 8px; padding: 8px 24px; background: transparent;
           border: 1px solid #0f0; color: #0f0; font-family: monospace;
           font-size: 13px; cursor: pointer; letter-spacing: .1em; }}
  button:hover {{ background: #0f0; color: #000; }}
  nav {{ padding: 8px; font-size: 12px; }}
  nav a {{ color: #080; margin: 0 8px; text-decoration: none; }}
  nav a:hover {{ color: #0f0; }}
</style>
</head>
<body>
<video id="v" autoplay playsinline muted></video>
<div id="label">Camera {cam_idx} · source={source}</div>
<div id="status">DISCONNECTED</div>
<button onclick="connect()">CONNECT</button>
<nav id="nav"></nav>
<script>
const CAM_IDX = "{cam_idx}";
const ALL_CAMS = {list(SOURCE_MAP.keys())};

const nav = document.getElementById('nav');
ALL_CAMS.forEach(c => {{
  if (c !== CAM_IDX) {{
    const a = document.createElement('a');
    a.href = '/' + c + '/';
    a.textContent = 'Camera ' + c;
    nav.appendChild(a);
  }}
}});

let pc;
async function connect() {{
  if (pc) {{ pc.close(); pc = null; }}
  document.getElementById('status').textContent = 'CONNECTING...';
  pc = new RTCPeerConnection({{ iceServers: [{{ urls: 'stun:stun.l.google.com:19302' }}] }});
  pc.addTransceiver('video', {{ direction: 'recvonly' }});
  pc.ontrack = e => {{ document.getElementById('v').srcObject = e.streams[0]; }};
  pc.onconnectionstatechange = () => {{
    document.getElementById('status').textContent = pc.connectionState.toUpperCase();
  }};
  const offer = await pc.createOffer();
  await pc.setLocalDescription(offer);
  const resp = await fetch('/' + CAM_IDX + '/whep', {{
    method: 'POST',
    headers: {{ 'Content-Type': 'application/sdp' }},
    body: offer.sdp
  }});
  const answerSdp = await resp.text();
  await pc.setRemoteDescription({{ type: 'answer', sdp: answerSdp }});
}}
</script>
</body>
</html>"""


async def index(request):
    cam_idx = request.match_info["cam_idx"]
    if cam_idx not in SOURCE_MAP:
        return web.Response(status=404, text=f"Camera index '{cam_idx}' not found")
    return web.Response(
        content_type="text/html",
        text=make_viewer_html(cam_idx, SOURCE_MAP[cam_idx]),
    )


# ── Root redirect → first camera ──────────────────────────────────────────────

async def root_redirect(request):
    first = next(iter(SOURCE_MAP))
    raise web.HTTPFound(f"/{first}/")


# ── App lifecycle ──────────────────────────────────────────────────────────────

async def on_startup(app):
    for cam_idx, buf in app["buffers"].items():
        buf.start()
        logger.info("Camera %s → source %s", cam_idx, buf.source)


async def on_shutdown(app):
    for buf in app["buffers"].values():
        buf.stop()
    await asyncio.gather(*[pc.close() for pc in pcs.values()])
    pcs.clear()


# ── Entry point ────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="YOLO WHEP multi-source server")
    parser.add_argument("--conf-thresh", type=float, default=0.5)
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--cert", default="cert.pem")
    parser.add_argument("--key", default="key.pem")
    args = parser.parse_args()

    buffers: dict[str, YOLOFrameBuffer] = {
        cam_idx: YOLOFrameBuffer(source=src, conf_thresh=args.conf_thresh)
        for cam_idx, src in SOURCE_MAP.items()
    }

    app = web.Application()
    app["buffers"] = buffers
    app["fps"] = args.fps
    app.on_startup.append(on_startup)
    app.on_shutdown.append(on_shutdown)

    app.router.add_get("/", root_redirect)
    app.router.add_get("/detection", detection_stream)
    app.router.add_get("/{cam_idx}/", index)
    app.router.add_route("OPTIONS", "/{cam_idx}/whep", whep_options)
    app.router.add_post("/{cam_idx}/whep", whep_post)
    app.router.add_delete("/{cam_idx}/whep/{session_id}", whep_delete)

    ssl_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ssl_ctx.load_cert_chain(args.cert, args.key)

    logger.info("Server ready:")
    for cam_idx, src in SOURCE_MAP.items():
        logger.info("  https://0.0.0.0:%d/%s/   (source=%s)", args.port, cam_idx, src)
    logger.info("  https://0.0.0.0:%d/detection", args.port)
    web.run_app(app, host="0.0.0.0", port=args.port, ssl_context=ssl_ctx)


if __name__ == "__main__":
    main()
