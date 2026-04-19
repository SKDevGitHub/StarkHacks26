"""
YOLO WHEP Streaming Server
---------------------------
Streams annotated YOLO person-detection frames via WHEP
(WebRTC-HTTP Egress Protocol - RFC draft).

WHEP is a single HTTP POST endpoint — any standards-compliant
WebRTC client (browser, Quest, VLC, ffplay, Unity) can connect
with no custom signaling code.

Requirements:
    pip install aiortc aiohttp av ultralytics opencv-python

Usage:
    python yolo_whep_server.py --source 0 --port 8080

Connect from Quest browser:
    https://10.10.9.81:8080/whep

Connect from any browser:
    Open https://10.10.9.81:8080  (built-in minimal viewer)
"""

import argparse
import asyncio
import fractions
import json
import logging
import os
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

PERSON_CLASS = 0
pcs: dict[str, RTCPeerConnection] = {}


# ── YOLO frame buffer ──────────────────────────────────────────────────────────

class YOLOFrameBuffer:
    def __init__(self, source, conf_thresh):
        self.source = source
        self.conf_thresh = conf_thresh
        self._frame = None
        self._running = False
        self.model = YOLO("yolov8n.pt")

    def start(self):
        self._running = True
        Thread(target=self._run, daemon=True).start()

    def stop(self):
        self._running = False

    def latest_frame(self):
        return self._frame

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
            frame = r.orig_img.copy()
            for box in r.boxes:
                x1, y1, x2, y2 = map(int, box.xyxy[0])
                conf = float(box.conf[0])
                cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 220, 90), 2)
                cv2.putText(frame, f"person {conf:.2f}", (x1, y1 - 8),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 220, 90), 2)
            cv2.putText(frame, f"People: {len(r.boxes)}", (12, 32),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 255), 2)
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


# ── WHEP endpoint ──────────────────────────────────────────────────────────────

async def whep_options(request):
    """WHEP preflight / capability discovery."""
    return web.Response(
        status=200,
        headers={
            **CORS,
            "Access-Control-Expose-Headers": "Link, Location, ETag",
            "Link": '</whep>; rel="urn:ietf:params:whep:ext:core:server-sent-events"',
        }
    )


async def whep_post(request):
    """
    WHEP POST — client sends SDP offer in body (application/sdp),
    server responds 201 Created with SDP answer.
    """
    body = await request.text()

    # Accept either raw SDP or JSON {sdp, type} for compatibility
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
        logger.info("[%s] state → %s", session_id[:8], pc.connectionState)
        if pc.connectionState in ("failed", "closed"):
            await pc.close()
            pcs.pop(session_id, None)

    buf: YOLOFrameBuffer = request.app["yolo_buffer"]
    fps: int = request.app["fps"]
    pc.addTrack(YOLOVideoTrack(buf, fps))

    await pc.setRemoteDescription(offer_sdp)
    answer = await pc.createAnswer()
    await pc.setLocalDescription(answer)

    logger.info("[%s] WHEP session created", session_id[:8])

    return web.Response(
        status=201,
        content_type="application/sdp",
        headers={
            **CORS,
            "Location": f"/whep/{session_id}",
            "ETag": f'"{session_id}"',
        },
        text=pc.localDescription.sdp,
    )


async def whep_delete(request):
    """WHEP DELETE — client tears down a session."""
    session_id = request.match_info["session_id"]
    pc = pcs.pop(session_id, None)
    if pc:
        await pc.close()
        logger.info("[%s] WHEP session deleted", session_id[:8])
        return web.Response(status=200, headers=CORS)
    return web.Response(status=404, headers=CORS)


# ── Minimal built-in viewer ────────────────────────────────────────────────────

VIEWER_HTML = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>YOLO · WHEP</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { background: #000; display: flex; flex-direction: column;
         align-items: center; justify-content: center;
         min-height: 100vh; font-family: monospace; color: #0f0; }
  video { width: 100vw; max-width: 1280px; aspect-ratio: 16/9;
          display: block; background: #000; }
  #status { padding: 8px 16px; font-size: 13px; letter-spacing: .1em; }
  button { margin: 8px; padding: 8px 24px; background: transparent;
           border: 1px solid #0f0; color: #0f0; font-family: monospace;
           font-size: 13px; cursor: pointer; letter-spacing: .1em; }
  button:hover { background: #0f0; color: #000; }
</style>
</head>
<body>
<video id="v" autoplay playsinline muted></video>
<div id="status">DISCONNECTED</div>
<button onclick="connect()">CONNECT</button>
<script>
let pc;
async function connect() {
  if (pc) { pc.close(); pc = null; }
  document.getElementById('status').textContent = 'CONNECTING...';
  pc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] });
  pc.addTransceiver('video', { direction: 'recvonly' });
  pc.ontrack = e => { document.getElementById('v').srcObject = e.streams[0]; };
  pc.onconnectionstatechange = () => {
    document.getElementById('status').textContent = pc.connectionState.toUpperCase();
  };
  const offer = await pc.createOffer();
  await pc.setLocalDescription(offer);
  const resp = await fetch('/whep', {
    method: 'POST',
    headers: { 'Content-Type': 'application/sdp' },
    body: offer.sdp
  });
  const answerSdp = await resp.text();
  await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
}
</script>
</body>
</html>"""


async def index(request):
    return web.Response(content_type="text/html", text=VIEWER_HTML)


# ── App lifecycle ──────────────────────────────────────────────────────────────

async def on_startup(app):
    app["yolo_buffer"].start()
    logger.info("YOLO inference started (source=%s)", app["yolo_buffer"].source)


async def on_shutdown(app):
    app["yolo_buffer"].stop()
    await asyncio.gather(*[pc.close() for pc in pcs.values()])
    pcs.clear()


# ── Entry point ────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="YOLO WHEP streaming server")
    parser.add_argument("--source", type=int, default=0)
    parser.add_argument("--conf-thresh", type=float, default=0.5)
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--fps", type=int, default=30)
    parser.add_argument("--cert", default="cert.pem")
    parser.add_argument("--key", default="key.pem")
    args = parser.parse_args()

    buf = YOLOFrameBuffer(source=args.source, conf_thresh=args.conf_thresh)

    app = web.Application()
    app["yolo_buffer"] = buf
    app["fps"] = args.fps
    app.on_startup.append(on_startup)
    app.on_shutdown.append(on_shutdown)

    # Routes
    #app.router.add_get("/", index)
    app.router.add_route("OPTIONS", "/whep", whep_options)
    app.router.add_post("/whep", whep_post)
    app.router.add_delete("/whep/{session_id}", whep_delete)

    ssl_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ssl_ctx.load_cert_chain(args.cert, args.key)

    logger.info("WHEP server → https://0.0.0.0:%d/whep", args.port)
    logger.info("Viewer      → https://0.0.0.0:%d/", args.port)
    web.run_app(app, host="0.0.0.0", port=args.port, ssl_context=ssl_ctx)


if __name__ == "__main__":
    main()
