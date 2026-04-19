import argparse
import cv2
from ultralytics import YOLO

PERSON_CLASS = 0


def detect_stream(source, conf_thresh):
    model = YOLO("yolov8n.pt")

    for r in model(source, classes=[PERSON_CLASS], conf=conf_thresh,
                   stream=True, verbose=False):
        frame = r.orig_img

        for box in r.boxes:
            x1, y1, x2, y2 = map(int, box.xyxy[0])
            conf = float(box.conf[0])
            cv2.rectangle(frame, (x1, y1), (x2, y2), (0, 220, 90), 2)
            cv2.putText(frame, f"person {conf:.2f}", (x1, y1 - 8),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 220, 90), 2)

        cv2.putText(frame, f"People: {len(r.boxes)}", (12, 32),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 255), 2)

        cv2.imshow("People Detector", frame)
        if cv2.waitKey(1) & 0xFF == ord("q"):
            break

    cv2.destroyAllWindows()


def main():
    parser = argparse.ArgumentParser(description="Detect people from a camera or video")
    parser.add_argument("--source", type=int, default=0, help="Webcam index (default: 0)")
    parser.add_argument("--conf-thresh", type=float, default=0.5, help="Confidence threshold")
    args = parser.parse_args()

    detect_stream(args.source, args.conf_thresh)


if __name__ == "__main__":
    main()