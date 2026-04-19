import cv2
import threading
import numpy as np
import time

class CameraStream:
    def __init__(self, device_path, width=320, height=240, name="Cam"):
        self.name = name
        self.frame = None
        self.running = True
        self.lock = threading.Lock()

        print(f"  [{name}] Opening {device_path}...")
        self.cap = cv2.VideoCapture(device_path, cv2.CAP_V4L2)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        self.cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
        self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)

        if not self.cap.isOpened():
            raise RuntimeError(f"Could not open {name} ({device_path})")

        # Warm up — drain a few frames before handing off to thread
        print(f"  [{name}] Warming up...")
        for _ in range(5):
            self.cap.read()

        print(f"  [{name}] Ready")

    def start(self):
        threading.Thread(target=self._loop, daemon=True).start()
        return self

    def _loop(self):
        while self.running:
            ret, frame = self.cap.read()
            if ret:
                with self.lock:
                    self.frame = frame

    def read(self):
        with self.lock:
            return self.frame.copy() if self.frame is not None else None

    def stop(self):
        self.running = False
        self.cap.release()


devices = [
    ("/dev/video0", "Cam0"),
    ("/dev/video2", "Cam2"),
    ("/dev/video4", "Cam4"),
]

streams = []
for path, name in devices:
    try:
        s = CameraStream(path, name=name)
        streams.append(s)
        time.sleep(1.5)  # stagger each open — give V4L2 time to settle
    except RuntimeError as e:
        print(f"  FAILED: {e}")

# Start all capture threads together after all devices are open
for s in streams:
    s.start()

try:
    while True:
        frames = [s.read() for s in streams]
        display = [
            f if f is not None else np.zeros((480, 640, 3), np.uint8)
            for f in frames
        ]
        cv2.imshow("Cameras", np.hstack(display))
        if cv2.waitKey(1) & 0xFF == ord('q'):
            break
finally:
    for s in streams:
        s.stop()
    cv2.destroyAllWindows()
